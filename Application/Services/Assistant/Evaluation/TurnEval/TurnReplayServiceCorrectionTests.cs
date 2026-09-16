// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The turn-eval replay has to walk the same correction path production walks, or a goldset item measures
/// something the live pipeline never does: plan on an anchor rebuilt from the item (never a table row),
/// assemble on the composite with the exclusion and without pins, complete the correction, and answer a
/// clarification deterministically without a provider call. It must do all of that without a single write
/// - the class summary promises that the only persistence of a replay is the EvalRun of the runner.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnReplayServiceCorrectionTests
{
    private const string ModelId = "model-under-replay";
    private const string ItemId = "cr-de-005-ambiguous";
    private const string CorrectionMessage = "Nein, ich meinte alle Mitarbeitenden.";
    private const string PreviousMessage = "Trag alle Mitarbeitenden in die Gruppe ein.";
    private const string PreviousSkill = "find_customer_candidates";
    private const string ReadOnlyPreviousSkill = "get_client_by_name";
    private const string WritePreviousSkill = "create_group";
    private const string PreviousLabel = "Searches for customers by name.";
    private const string UndoneSkill = "add_shift_to_group";
    private const string UndoSkill = "remove_shift_from_group";
    private const string Composite = "composite of both messages";
    private const string ContextNote = "CORRECTION - the previous turn searched for customers.";
    private const string ClarificationReply =
        "I searched for customers. Did you mean adding clients to a group, or listing them?";
    private const string ModelAnswer = "Die Mitarbeitenden sind eingetragen.";
    private const string Locale = "de";
    private const string MutatingCorrection = "Nein, erstelle stattdessen eine neue Gruppe.";
    private const int AssemblyDelayMs = 5;
    private const int SpinIterations = 64;

    private static readonly string UserId = Guid.NewGuid().ToString();

    private ISkillToolsetAssembler _assembler = null!;
    private LLMContext? _capturedContext;
    private ITurnPreparationService _turnPreparation = null!;
    private ILLMProvider _provider = null!;
    private TurnReplayService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _assembler = Substitute.For<ISkillToolsetAssembler>();
        _assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new SkillToolsetResult());

        _turnPreparation = Substitute.For<ITurnPreparationService>();

        _provider = Substitute.For<ILLMProvider>();
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = true, Content = ModelAnswer });

        var repository = Substitute.For<ILLMRepository>();
        repository.GetModelByIdAsync(ModelId).Returns(new LLMModel
        {
            ModelId = ModelId,
            ApiModelId = ModelId,
            ProviderId = "test-provider",
            IsEnabled = true,
            MaxTokens = 4096
        });

        var providerFactory = Substitute.For<ILLMProviderFactory>();
        providerFactory.GetProviderForModelAsync(ModelId).Returns(_provider);

        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        var contextBudgetPolicy = Substitute.For<IContextBudgetPolicy>();
        contextBudgetPolicy.Resolve(Arg.Any<ILLMProvider>(), Arg.Any<LLMModel>())
            .Returns(new ContextBudgetProfile(20, 30, 5, 5, 8_000));

        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetNowAsync(Arg.Any<CancellationToken>()).Returns(DateTimeOffset.UtcNow);
        companyClock.GetTimeZoneResolutionAsync(Arg.Any<CancellationToken>())
            .Returns(new CompanyTimeZoneResolution(TimeZoneInfo.Utc, CompanyTimeZoneSource.Utc));

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _capturedContext = null;
        var planningScopeEnricher = Substitute.For<IPlanningScopeEnricher>();
        planningScopeEnricher
            .When(enricher => enricher.EnrichAsync(Arg.Any<LLMContext>(), Arg.Any<CancellationToken>()))
            .Do(call => _capturedContext = call.Arg<LLMContext>());

        _service = new TurnReplayService(
            skillCache,
            _assembler,
            planningScopeEnricher,
            Substitute.For<IEntityCandidateGrounder>(),
            new LLMProviderOrchestrator(
                Substitute.For<ILogger<LLMProviderOrchestrator>>(), providerFactory, repository),
            contextAssemblyPipeline: null!,
            new LLMSystemPromptBuilder(
                new PromptTranslationProvider(
                    scopeFactory, Substitute.For<ILogger<PromptTranslationProvider>>()),
                companyClock),
            scopeFactory,
            contextBudgetPolicy,
            _turnPreparation,
            Substitute.For<ILogger<TurnReplayService>>());
    }

    private static TurnGoldsetItem Item(string calledSkill = PreviousSkill) => new()
    {
        Id = ItemId,
        Message = CorrectionMessage,
        Locale = Locale,
        PreviousTurn = new TurnGoldsetPreviousTurn
        {
            Message = PreviousMessage,
            CalledSkill = calledSkill,
            SkillDisplayLabel = PreviousLabel,
            AssistantAnswerExcerpt = "Ich habe nach Kunden gesucht."
        }
    };

    private static TurnGoldsetItem MutatingItem()
    {
        var item = Item();
        item.Message = MutatingCorrection;
        return item;
    }

    // The undo the harness scores (TurnEvalScorer.UndoOfferedWhenExpected) is built by the real resolver
    // from the anchor this service rebuilds, so the two halves are asserted together: the anchor has to
    // carry enough for an undo to exist at all, and the result has to report it.
    private static TurnGoldsetItem UndoItem()
    {
        var item = Item(UndoneSkill);
        item.PreviousTurn!.Arguments = new Dictionary<string, string>
        {
            ["shiftId"] = "6f0f1f7a-0000-4000-8000-000000000001",
            ["groupId"] = "6f0f1f7a-0000-4000-8000-000000000002"
        };

        return item;
    }

    [Test]
    public void TheRebuiltAnchor_CarriesWhatAnUndoNeeds()
    {
        var anchor = TurnReplayService.BuildReplayLastAction(UndoItem(), UserId);

        anchor.ShouldNotBeNull();
        new SkillInverseResolver().TryResolve(anchor!.Calls[0], out var undo).ShouldBeTrue();
        undo!.SkillName.ShouldBe(UndoSkill);
    }

    [Test]
    public async Task AnOfferedUndo_IsReportedOnTheResult()
    {
        GivenACorrectionIsPlanned(null, new SkillUndoInvocation(
            UndoSkill, new Dictionary<string, object> { ["shiftId"] = "s-1" }));

        var result = await Replay(UndoItem());

        result.UndoOfferedSkill.ShouldBe(UndoSkill);
    }

    [Test]
    public async Task WithoutAnUndo_TheResultReportsNone()
    {
        GivenACorrectionIsPlanned(null);

        var result = await Replay(Item());

        result.UndoOfferedSkill.ShouldBeNull();
    }

    private void GivenACorrectionIsPlanned(string? clarificationReply, SkillUndoInvocation? undo = null)
    {
        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns(call => new GracefulCorrectionPlan(
                call.Arg<GracefulCorrectionInput>().LastAction!, CorrectionMessage, Composite,
                new[] { PreviousSkill }));

        _turnPreparation.CompleteCorrection(
                Arg.Any<GracefulCorrectionPlan>(), Arg.Any<IReadOnlyList<LLMFunction>>(), Arg.Any<string?>())
            .Returns(new GracefulCorrectionOutcome(
                ContextNote, clarificationReply,
                clarificationReply == null ? [] : ["add_clients_to_group", "list_group_clients"],
                undo));
    }

    private Task<TurnReplayResult> Replay(TurnGoldsetItem item) =>
        _service.ReplayAsync(item, ModelId, UserId, new List<string>());

    [Test]
    public async Task WithoutACorrection_TheReplayRunsAsBefore()
    {
        var result = await Replay(Item());

        result.Success.ShouldBeTrue();
        result.Content.ShouldBe(ModelAnswer);
        result.CorrectionApplied.ShouldBeFalse();
        result.CorrectionClarificationOffered.ShouldBeFalse();
        await _provider.Received(1).ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task WithACorrection_TheReplayAssemblesOnTheCompositeWithTheExclusionAndNoPins()
    {
        GivenACorrectionIsPlanned(null);

        var result = await Replay(Item());

        result.CorrectionApplied.ShouldBeTrue();
        result.CorrectionClarificationOffered.ShouldBeFalse();
        await _assembler.Received(1).AssembleAsync(
            Arg.Any<Agent?>(), Arg.Any<List<string>>(), Composite, Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Is<IReadOnlyCollection<string>?>(excluded => excluded != null && excluded.Contains(PreviousSkill)),
            Arg.Is<IReadOnlyCollection<string>?>(pinned => pinned == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithACorrection_TheNoteReachesTheProvidersVolatileSystemPrompt()
    {
        GivenACorrectionIsPlanned(null);
        LLMProviderRequest? captured = null;
        _provider.ProcessAsync(Arg.Do<LLMProviderRequest>(request => captured = request))
            .Returns(new LLMProviderResponse { Success = true, Content = ModelAnswer });

        await Replay(Item());

        captured.ShouldNotBeNull();
        captured!.VolatileSystemPrompt.ShouldContain(ContextNote);
    }

    [Test]
    public async Task WithAClarification_TheReplayAnswersDeterministicallyAndCallsNoProvider()
    {
        GivenACorrectionIsPlanned(ClarificationReply);

        var result = await Replay(Item());

        result.Success.ShouldBeTrue();
        result.Content.ShouldBe(ClarificationReply);
        result.ChosenTool.ShouldBeNull();
        result.CorrectionApplied.ShouldBeTrue();
        result.CorrectionClarificationOffered.ShouldBeTrue();
        await _provider.DidNotReceiveWithAnyArgs().ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    // A clarification item is measured like any other: the work it did (assembly, planning, completion)
    // is real time, and the tool-choice the turn would have forced is reported even though no provider
    // call happened. Reporting zero would make correction items look free in every latency comparison.
    [Test]
    public async Task WithAClarification_TheReplayReportsItsOwnLatencyAndToolChoice()
    {
        GivenACorrectionIsPlanned(ClarificationReply);
        _assembler.AssembleAsync(
                Arg.Any<Agent?>(), Arg.Any<List<string>>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                SpinForAssemblyDelay();
                return Task.FromResult(new SkillToolsetResult());
            });

        var result = await Replay(MutatingItem());

        result.LatencyMs.ShouldBeGreaterThanOrEqualTo(AssemblyDelayMs);
        result.ToolChoiceRequired.ShouldBeTrue();
    }

    /// <summary>
    /// Burns AssemblyDelayMs of the SAME clock the replay measures itself with. A Task.Delay here made
    /// the assertion compare a timer against a stopwatch, which flaked; a spin on a Stopwatch that the
    /// replay's own stopwatch already encloses is greater-or-equal by construction, so the delay can stay
    /// small enough not to slow the suite down.
    /// </summary>
    private static void SpinForAssemblyDelay()
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < AssemblyDelayMs)
        {
            Thread.SpinWait(SpinIterations);
        }
    }

    // The anchor is rebuilt from the goldset item, not read from a table, and a replay never has a live
    // recipe: an item that would otherwise be rejected by gate G1 must be measurable.
    [Test]
    public async Task ThePlanningInput_IsBuiltFromTheItemAndNeverDeclaresALiveRecipe()
    {
        GracefulCorrectionInput? captured = null;
        _turnPreparation.PlanCorrectionAsync(
                Arg.Do<GracefulCorrectionInput>(input => captured = input), Arg.Any<CancellationToken>())
            .Returns((GracefulCorrectionPlan?)null);

        await Replay(Item());

        captured.ShouldNotBeNull();
        captured!.RecipeIsActive.ShouldBeFalse();
        captured.ConversationId.ShouldBeNull();
        captured.Message.ShouldBe(CorrectionMessage);
        captured.LastAction.ShouldNotBeNull();
        captured.LastAction!.UserMessage.ShouldBe(PreviousMessage);
        captured.LastAction.Calls.Single().SkillName.ShouldBe(PreviousSkill);
    }

    [Test]
    public async Task AnItemWithoutAPreviousTurn_PlansWithoutAnAnchor()
    {
        GracefulCorrectionInput? captured = null;
        _turnPreparation.PlanCorrectionAsync(
                Arg.Do<GracefulCorrectionInput>(input => captured = input), Arg.Any<CancellationToken>())
            .Returns((GracefulCorrectionPlan?)null);

        await Replay(new TurnGoldsetItem { Id = ItemId, Message = CorrectionMessage, Locale = Locale });

        captured.ShouldNotBeNull();
        captured!.LastAction.ShouldBeNull();
    }

    // Side-effect freedom, from both ends: the replay never records a previous action (the one write the
    // turn-preparation interface offers), and it is not even wired to a store that could hold one.
    [Test]
    public async Task TheReplay_NeverRecordsAPreviousAction()
    {
        GivenACorrectionIsPlanned(ClarificationReply);

        await Replay(Item());

        _turnPreparation.DidNotReceiveWithAnyArgs().RecordLastAction(
            Arg.Any<LLMContext>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<LLMFunctionCall>>(), Arg.Any<bool>());
    }

    // Side-effect freedom on the context too. GracefulCorrectionApplied is the replay's own record that
    // it corrected; CorrectionUndoOffered must stay false because the replay holds no token - claiming
    // one would tell a following turn to leave an outstanding row unsettled.
    [Test]
    public async Task TheReplayContext_ReportsTheCorrectionButNeverAHeldUndo()
    {
        GivenACorrectionIsPlanned(null, new SkillUndoInvocation(
            UndoSkill, new Dictionary<string, object> { ["shiftId"] = "s-1" }));

        await Replay(UndoItem());

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.GracefulCorrectionApplied.ShouldBeTrue();
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public async Task TheReplayContext_WithoutACorrection_ReportsNoCorrectionAtAll()
    {
        _turnPreparation.PlanCorrectionAsync(Arg.Any<GracefulCorrectionInput>(), Arg.Any<CancellationToken>())
            .Returns((GracefulCorrectionPlan?)null);

        await Replay(Item());

        _capturedContext.ShouldNotBeNull();
        _capturedContext!.GracefulCorrectionApplied.ShouldBeFalse();
        _capturedContext.CorrectionUndoOffered.ShouldBeFalse();
    }

    [Test]
    public void TheReplay_DependsOnNoStoreThatCouldBeWritten()
    {
        var dependencies = typeof(TurnReplayService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToList();

        dependencies.ShouldNotContain(typeof(IAssistantLastActionStore));
        dependencies.ShouldNotContain(typeof(IPendingConfirmationStore));
        dependencies.ShouldNotContain(typeof(IPendingRecipeStore));
    }

    [Test]
    public void BuildReplayLastAction_WithoutAPreviousTurn_ReturnsNothing() =>
        TurnReplayService.BuildReplayLastAction(
            new TurnGoldsetItem { Id = ItemId, Message = CorrectionMessage }, UserId).ShouldBeNull();

    [Test]
    public void BuildReplayLastAction_DerivesTheReadOnlyFlagFromTheSkillName()
    {
        TurnReplayService.BuildReplayLastAction(Item(ReadOnlyPreviousSkill), UserId)!
            .Calls.Single().IsReadOnly.ShouldBeTrue();

        TurnReplayService.BuildReplayLastAction(Item(WritePreviousSkill), UserId)!
            .Calls.Single().IsReadOnly.ShouldBeFalse();
    }

    [Test]
    public void BuildReplayLastAction_KeysTheRecordOnTheItemAndTheReplayUser()
    {
        var lastAction = TurnReplayService.BuildReplayLastAction(Item(), UserId)!;

        lastAction.UserId.ShouldBe(Guid.Parse(UserId));
        lastAction.ConversationId.ShouldBe(ItemId);
        lastAction.Calls.Single().SkillDisplayLabel.ShouldBe(PreviousLabel);
    }

    /// <summary>
    /// The clarification resolves the previous action from the authored label DICTIONARY in the
    /// correction turn's language, and a goldset item carries only one label. Presenting it as the label
    /// for the item's own locale is what keeps the question reachable in a replay at all - without it
    /// every correction item would score as "no question asked" for a reason that exists nowhere in
    /// production.
    /// </summary>
    [Test]
    public void BuildReplayLastAction_PresentsTheGoldsetLabelAsTheLabelOfTheItemsLocale()
    {
        var labels = TurnReplayService.BuildReplayLastAction(Item(), UserId)!.Calls.Single().SkillLabels;

        labels.ShouldNotBeNull();
        labels![Locale].ShouldBe(PreviousLabel);
    }

    [Test]
    public void BuildReplayLastAction_WithoutAGoldsetLabel_CarriesNoAuthoredLabels()
    {
        var item = Item();
        item.PreviousTurn!.SkillDisplayLabel = null;

        TurnReplayService.BuildReplayLastAction(item, UserId)!.Calls.Single().SkillLabels.ShouldBeNull();
    }
}
