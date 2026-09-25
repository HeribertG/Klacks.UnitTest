// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The slot-extraction model call of a freshly matched recipe runs with a token linked to the turn's stop
/// token, so a stop ends the call instead of letting it run to its end before the turn reaches its next
/// safe point. The extractor degrades every failure to "no slots", so the stop shows as an empty
/// extraction and never as an exception; a call without a stop keeps the token of the request only.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationStopTests
{
    private const string ConversationId = "conv-stop-extraction";
    private const string RecipeName = "add-employee-to-group";
    private const string AskSlot = "groupName";
    private const string Message = "Ich möchte einen Mitarbeiter in eine Gruppe eintragen.";
    private const int SlowCallMs = 5_000;

    private readonly Guid _userId = Guid.NewGuid();

    private RecipeEngineService _recipeEngine = null!;

    [SetUp]
    public void SetUp()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        var recipe = new AgentRecipe
        {
            Name = RecipeName,
            IsEnabled = true,
            Goal = "Add an employee to a group.",
            TriggerJson = """{"allOf":[{"anyWordStart":["mitarbeiter"]},{"anyWordStart":["gruppe"]}],"noneOf":[]}""",
            StepsJson = "[{\"kind\":\"ask\",\"slot\":\"" + AskSlot + "\",\"prompt\":\"Which group?\"}]"
        };
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe> { recipe });
        recipeRepository.GetByNameAsync(RecipeName, Arg.Any<CancellationToken>()).Returns(recipe);

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), NullLogger<RecipeEngineService>.Instance);
    }

    [Test]
    public async Task AStopDuringTheSlotExtraction_CancelsTheCallAndTheRecipeStillEngagesWithoutSlots()
    {
        using var stop = new CancellationTokenSource();
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                stop.Cancel();
                await Task.Delay(SlowCallMs, call.Arg<CancellationToken>());
                return new LLMProviderResponse { Success = true, Content = "{\"" + AskSlot + "\":\"Zurich\"}" };
            });

        var plan = await Resolve(provider, stop.Token);

        plan.ShouldNotBeNull();
        plan!.Name.ShouldBe(RecipeName);
        plan.Slots.ShouldNotContainKey(AskSlot);
    }

    [Test]
    public async Task WithoutAStop_TheExtractionRunsToItsEndAndFillsTheSlot()
    {
        var provider = Substitute.For<ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = "{\"" + AskSlot + "\":\"Zurich\"}" });

        var plan = await Resolve(provider, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan!.Slots[AskSlot].ShouldBe("Zurich");
    }

    private Task<RecipeExecutionPlan?> Resolve(ILLMProvider provider, CancellationToken stopToken) =>
        Subject().ResolveOrResumeRecipeAsync(
            new LLMContext
            {
                Message = Message,
                UserId = _userId.ToString(),
                ConversationId = ConversationId,
                Language = "de",
                StopToken = stopToken,
                AvailableFunctions = []
            },
            provider, new LLMModel { ApiModelId = "m" },
            ConversationId, pendingConfirmationForced: false, CancellationToken.None);

    private TurnPreparationService Subject() => new(
        Substitute.For<IPendingConfirmationStore>(),
        _recipeEngine,
        Substitute.For<IRecipeRunRecorder>(),
        new RecipeSlotExtractor(NullLogger<RecipeSlotExtractor>.Instance),
        Substitute.For<IAssistantLastActionStore>(),
        Substitute.For<IDeterministicRouteProbe>(),
        Substitute.For<ISkillInverseResolver>(),
        Substitute.For<ILogger<TurnPreparationService>>());
}
