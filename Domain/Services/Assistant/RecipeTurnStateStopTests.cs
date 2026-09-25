// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A stop that arrives while the recipe is being prepared (the slot-extraction call of a freshly matched
/// recipe) must not leave a Running recipe-run row behind: the turn ends at its next safe point without
/// running the recipe, so nothing would ever close the row. The extractor itself treats the stop as the quiet
/// event it is and logs no warning with a stack trace for it, while a genuine failure is still a warning.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTurnStateStopTests
{
    private const string ConversationId = "conv-recipe-stop";
    private const string RecipeName = "guided-setup";
    private const string SlotName = "groupName";

    private readonly Guid _userId = Guid.NewGuid();

    private ITurnPreparationService _preparation = null!;
    private IRecipeRunRecorder _recorder = null!;
    private RecipeEngineService _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _preparation = Substitute.For<ITurnPreparationService>();
        _recorder = Substitute.For<IRecipeRunRecorder>();
        _engine = new RecipeEngineService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IPendingRecipeStore>(),
            NullLogger<RecipeEngineService>.Instance);
    }

    [Test]
    public async Task AStopDuringThePreparation_LeavesNoRunRow()
    {
        using var stop = new CancellationTokenSource();
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stop.Cancel();
            return new TurnPreparation(Plan(), false, null, null);
        });

        await BeginAsync(stop.Token);

        await _recorder.DidNotReceiveWithAnyArgs().BeginOrResumeAsync(default!, default, default!, default, default, default);
    }

    [Test]
    public async Task AStopThatAlreadyWasRequestedBeforeTheTurnBegan_LeavesNoRunRow()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(Plan(), false, null, null));

        await BeginAsync(stop.Token);

        await _recorder.DidNotReceiveWithAnyArgs().BeginOrResumeAsync(default!, default, default!, default, default, default);
    }

    [Test]
    public async Task WithoutAStop_TheRunRowIsBegunAsBefore()
    {
        using var stop = new CancellationTokenSource();
        _preparation.PrepareAsync(Arg.Any<TurnPreparationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnPreparation(Plan(), false, null, null));

        await BeginAsync(stop.Token);

        await _recorder.Received(1).BeginOrResumeAsync(
            RecipeName, _userId, ConversationId, Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheExtractorTreatsAStopAsAQuietEvent_AndStillWarnsAboutARealFailure()
    {
        var logger = new RecordingLogger<RecipeSlotExtractor>();
        var extractor = new RecipeSlotExtractor(logger);
        var hints = new Dictionary<string, string> { [SlotName] = "The group" };
        var model = new LLMModel { ApiModelId = "m" };
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var stopped = Substitute.For<ILLMProvider>();
        stopped.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns<LLMProviderResponse>(call => throw new OperationCanceledException(call.Arg<CancellationToken>()));

        var slots = await extractor.ExtractAsync(stopped, model, "Add someone to a group", hints, stop.Token);

        slots.ShouldBeEmpty();
        logger.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);

        var broken = Substitute.For<ILLMProvider>();
        broken.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns<LLMProviderResponse>(_ => throw new InvalidOperationException("provider down"));

        (await extractor.ExtractAsync(broken, model, "Add someone to a group", hints, CancellationToken.None)).ShouldBeEmpty();

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    private Task<RecipeTurnState> BeginAsync(CancellationToken stopToken)
    {
        var context = new LLMContext
        {
            Message = "Add someone to a group",
            UserId = _userId.ToString(),
            TurnId = Guid.NewGuid(),
            StopToken = stopToken
        };

        return RecipeTurnState.BeginAsync(
            _preparation, _recorder, _engine, NullLogger.Instance, context,
            Substitute.For<ILLMProvider>(), new LLMModel { ApiModelId = "m" }, ConversationId, CancellationToken.None);
    }

    private static RecipeExecutionPlan Plan() => new(
        RecipeName,
        [new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = SlotName, Prompt = "Which group?" }],
        needsConfirmation: true);
}
