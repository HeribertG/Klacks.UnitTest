// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the planning and replaying half of the paired gate. The plan holds the holdout items of the
/// default goldset that involve the skill - previously passing first, capped, recipe items never - and the
/// train misses the proposal came from, resolved in whichever learning goldset they live. A replay is a hit
/// when the expected tool (or for a no-tool item: no tool) was chosen, a miss when anything else was chosen -
/// including a tool list the expected tool dropped out of - and unmeasured when the provider did not answer.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetHoldoutReplayGateTests
{
    private const string Sharpened = "list_clients";
    private const string Confused = "revenue_per_client";
    private const string Unrelated = "navigate_to";
    private const string Model = "deepseek-flash";
    private const string OtherModel = "deepseek-v4-pro";
    private const string ForeignGoldset = "turn-honesty-v1";

    private const string JapaneseLocale = "ja";

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid I18nRunId = Guid.NewGuid();

    private static readonly string[] HoldoutIds =
        [.. Enumerable.Range(1, 4000).Select(i => $"ts-{i:D4}").Where(GoldsetPartitioner.IsHoldout).Take(40)];

    private static readonly string TrainId =
        Enumerable.Range(1, 4000).Select(i => $"ts-{i:D4}").First(GoldsetPartitioner.IsTrain);

    private static readonly string ParaphraseId = TurnEvalDefaults.ParaphraseItemIdPrefix + "ts-0001-1";

    private IEvalRunRepository _evalRuns = null!;
    private IEvalRunItemRepository _evalRunItems = null!;
    private ITurnGoldsetLoader _goldsetLoader = null!;
    private ITurnReplayService _replayService = null!;
    private ISkillLearningOptionsProvider _learningOptions = null!;
    private GoldsetHoldoutReplayGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _evalRuns = Substitute.For<IEvalRunRepository>();
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Model, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = RunId, Model = Model });

        _evalRunItems = Substitute.For<IEvalRunItemRepository>();
        _evalRunItems.ListByRunAsync(RunId, Arg.Any<CancellationToken>()).Returns([]);

        _goldsetLoader = Substitute.For<ITurnGoldsetLoader>();
        _goldsetLoader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);

        _replayService = Substitute.For<ITurnReplayService>();

        _learningOptions = Substitute.For<ISkillLearningOptionsProvider>();
        _learningOptions.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new SkillLearningOptions(
                SkillLearningDefaults.MinOccurrences,
                SkillLearningDefaults.MinDistinctUsers,
                SkillLearningDefaults.PruneDays,
                SkillLearningDefaults.RetentionDays,
                ReferenceModel: Model));

        _gate = new GoldsetHoldoutReplayGate(
            _evalRuns, _evalRunItems, _goldsetLoader, _replayService, _learningOptions,
            NullLogger<GoldsetHoldoutReplayGate>.Instance);
    }

    [Test]
    public async Task WithoutAFullRun_NothingCanBePlanned()
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Model, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);

        (await _gate.PlanAsync(Sharpened, [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, TrainId)])).ShouldBeNull();
    }

    // The dev database also carries nightly full runs of other models. A run of a model that is not the
    // configured reference model must be treated exactly like no reference run at all.
    [Test]
    public async Task AFullRunOfAnotherModel_IsTreatedAsNoReferenceRun()
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Model, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, OtherModel, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = Guid.NewGuid(), Model = OtherModel });

        (await _gate.PlanAsync(Sharpened, [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, TrainId)])).ShouldBeNull();
    }

    // Neither KLACKSY_LEARNING_REFERENCE_MODEL nor a database default model resolved to anything: the gate
    // must not guess a model to query eval_runs with.
    [Test]
    public async Task WithoutAResolvedReferenceModel_NoRunIsQueriedAndNothingCanBePlanned()
    {
        _learningOptions.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new SkillLearningOptions(
                SkillLearningDefaults.MinOccurrences,
                SkillLearningDefaults.MinDistinctUsers,
                SkillLearningDefaults.PruneDays,
                SkillLearningDefaults.RetentionDays,
                ReferenceModel: null));

        (await _gate.PlanAsync(Sharpened, [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, TrainId)])).ShouldBeNull();

        await _evalRuns.DidNotReceive().GetLatestFullRunAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ThePlan_HoldsTheHoldoutItemsThatInvolveTheSkill()
    {
        GivenRows(
            Row(HoldoutIds[0], Sharpened, true),
            Row(HoldoutIds[1], Confused, false, chosenTool: Sharpened),
            Row(HoldoutIds[2], Unrelated, true),
            Row(TrainId, Sharpened, true));
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened), Item(HoldoutIds[1], Confused), Item(HoldoutIds[2], Unrelated), Item(TrainId, Sharpened));

        var plan = await _gate.PlanAsync(Sharpened, []);

        plan.ShouldNotBeNull();
        plan.HoldoutItems.Select(item => item.ItemId).ShouldBe([HoldoutIds[0], HoldoutIds[1]], ignoreOrder: true);
        plan.Model.ShouldBe(Model);
        plan.ReferenceEvalRunId.ShouldBe(RunId);
    }

    [Test]
    public async Task ThePlan_IsCappedAndKeepsThePreviouslyPassingItems()
    {
        var failing = HoldoutIds.Take(5).Select(id => Row(id, Sharpened, false));
        var passing = HoldoutIds.Skip(5).Take(30).Select(id => Row(id, Sharpened, true));
        GivenRows([.. failing, .. passing]);
        GivenBaseGoldset([.. HoldoutIds.Take(35).Select(id => Item(id, Sharpened))]);

        var plan = await _gate.PlanAsync(Sharpened, []);

        plan!.HoldoutItems.Count.ShouldBe(SkillLearningDefaults.MaxTargetedHoldoutReplaysPerProposal);
        plan.HoldoutItems.ShouldAllBe(item => !HoldoutIds.Take(5).Contains(item.ItemId));
    }

    [Test]
    public async Task ThePlan_AlsoHoldsTheTranslatedHoldoutItemsOfTheLatestI18nRun()
    {
        var translated = GoldsetTranslationId.Compose(JapaneseLocale, HoldoutIds[0]);
        GivenRows(Row(HoldoutIds[1], Sharpened, true));
        GivenBaseGoldset(Item(HoldoutIds[1], Sharpened));
        GivenI18nRun(Row(translated, Sharpened, true, runId: I18nRunId), Row(GoldsetTranslationId.Compose(JapaneseLocale, TrainId), Sharpened, true, runId: I18nRunId));
        GivenI18nGoldset(Item(translated, Sharpened), Item(GoldsetTranslationId.Compose(JapaneseLocale, TrainId), Sharpened));

        var plan = await _gate.PlanAsync(Sharpened, []);

        plan.ShouldNotBeNull();
        plan.HoldoutItems.ShouldBe(
            [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[1]), new GoldsetItemRef(TurnEvalDefaults.I18nGoldset, translated)],
            ignoreOrder: true);
        plan.ReferenceEvalRunId.ShouldBe(RunId);
    }

    [Test]
    public async Task WithoutAnI18nRun_ThePlanStillHoldsTheDefaultHoldoutItems()
    {
        GivenRows(Row(HoldoutIds[0], Sharpened, true));
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));

        var plan = await _gate.PlanAsync(Sharpened, []);

        plan!.HoldoutItems.ShouldBe([new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])]);
    }

    // "i18n-..." sorts before "ts-..." ordinally; one shared cap would let translations crowd out the default
    // goldset's own holdout items, so each goldset keeps its own cap.
    [Test]
    public async Task TranslatedHoldoutItems_DoNotCrowdOutTheDefaultOnes()
    {
        var translatedIds = HoldoutIds.Take(30).Select(id => GoldsetTranslationId.Compose(JapaneseLocale, id)).ToList();
        GivenRows([.. HoldoutIds.Take(30).Select(id => Row(id, Sharpened, true))]);
        GivenBaseGoldset([.. HoldoutIds.Take(30).Select(id => Item(id, Sharpened))]);
        GivenI18nRun([.. translatedIds.Select(id => Row(id, Sharpened, true, runId: I18nRunId))]);
        GivenI18nGoldset([.. translatedIds.Select(id => Item(id, Sharpened))]);

        var plan = await _gate.PlanAsync(Sharpened, []);

        plan!.HoldoutItems.Count(item => item.Goldset == TurnEvalDefaults.DefaultGoldset)
            .ShouldBe(SkillLearningDefaults.MaxTargetedHoldoutReplaysPerProposal);
        plan.HoldoutItems.Count(item => item.Goldset == TurnEvalDefaults.I18nGoldset)
            .ShouldBe(SkillLearningDefaults.MaxTargetedTranslatedHoldoutReplaysPerProposal);
    }

    // Ordinal ids put ar, cs, da ... first and th, vi, zh-CN, zh-TW last, so an ordinal cap of 15 over 24
    // translations of one source never replayed the late locales. The cap now takes a stable scatter.
    [Test]
    public async Task TheTranslatedHoldoutCap_DoesNotStarveTheLateLocales()
    {
        string[] locales =
        [
            "ar", "cs", "da", "el", "en", "es", "fi", "fr", "he", "id", "it", "ja",
            "ko", "ms", "nb", "nl", "pl", "pt", "ro", "sv", "th", "vi", "zh-CN", "zh-TW"
        ];
        var plannedLocales = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sources in HoldoutIds.Chunk(4).Take(4))
        {
            var translatedIds = sources.SelectMany(source => locales.Select(locale => GoldsetTranslationId.Compose(locale, source))).ToList();
            GivenI18nRun([.. translatedIds.Select(id => Row(id, Sharpened, true, runId: I18nRunId))]);
            GivenI18nGoldset([.. translatedIds.Select(id => Item(id, Sharpened))]);
            var gate = new GoldsetHoldoutReplayGate(
                _evalRuns, _evalRunItems, _goldsetLoader, _replayService, _learningOptions,
                NullLogger<GoldsetHoldoutReplayGate>.Instance);

            var plan = await gate.PlanAsync(Sharpened, []);

            var locales15 = plan!.HoldoutItems
                .Where(item => item.Goldset == TurnEvalDefaults.I18nGoldset)
                .Select(item => GoldsetTranslationId.TryParse(item.ItemId, out var locale, out _) ? locale : string.Empty)
                .ToList();
            locales15.Count.ShouldBe(SkillLearningDefaults.MaxTargetedTranslatedHoldoutReplaysPerProposal);
            locales15.ShouldBeUnique();
            plannedLocales.UnionWith(locales15);
        }

        plannedLocales.ShouldContain("zh-TW");
        plannedLocales.ShouldContain("th");
        plannedLocales.ShouldContain("vi");
    }

    [Test]
    public async Task ThePlan_ResolvesATrainMissOfTheI18nGoldset()
    {
        var translatedTrain = GoldsetTranslationId.Compose(JapaneseLocale, TrainId);
        GivenI18nGoldset(Item(translatedTrain, Sharpened));

        var plan = await _gate.PlanAsync(Sharpened, [new GoldsetItemRef(TurnEvalDefaults.I18nGoldset, translatedTrain)]);

        plan!.TrainItems.ShouldBe([new GoldsetItemRef(TurnEvalDefaults.I18nGoldset, translatedTrain)]);
    }

    [Test]
    public async Task ThePlan_ResolvesTrainMissesInBothLearningGoldsets()
    {
        GivenBaseGoldset(Item(TrainId, Sharpened));
        _goldsetLoader.LoadAsync(TurnEvalDefaults.ParaphraseGoldset, Arg.Any<CancellationToken>())
            .Returns([Item(ParaphraseId, Sharpened)]);

        var plan = await _gate.PlanAsync(Sharpened,
        [
            new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, TrainId),
            new GoldsetItemRef(TurnEvalDefaults.ParaphraseGoldset, ParaphraseId)
        ]);

        plan!.TrainItems.Count.ShouldBe(2);
        plan.HoldoutItems.ShouldBeEmpty();
    }

    [Test]
    public async Task HoldoutUnknownOrForeignTrainReferences_AreDropped()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened), Item(TrainId, Sharpened));

        var plan = await _gate.PlanAsync(Sharpened,
        [
            new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0]),
            new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, "ts-missing"),
            new GoldsetItemRef(ForeignGoldset, TrainId)
        ]);

        plan.ShouldNotBeNull();
        plan.HoldoutItems.ShouldBeEmpty();
        plan.TrainItems.ShouldBeEmpty();
    }

    // "Nothing to replay" against an existing reference run is permanent and must come back as an empty plan,
    // so the caller can close the proposal instead of keeping it in the pending window for ever.
    [Test]
    public async Task WithAReferenceRunButNothingToReplay_ThePlanIsEmptyNotNull()
    {
        var plan = await _gate.PlanAsync(Sharpened, []);

        plan.ShouldNotBeNull();
        plan.HoldoutItems.ShouldBeEmpty();
        plan.TrainItems.ShouldBeEmpty();
    }

    [Test]
    public async Task AnUnreadableParaphraseGoldset_LeavesTheRestOfThePlan()
    {
        GivenRows(Row(HoldoutIds[0], Sharpened, true));
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));
        _goldsetLoader.LoadAsync(TurnEvalDefaults.ParaphraseGoldset, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("not generated yet"));

        var plan = await _gate.PlanAsync(Sharpened, [new GoldsetItemRef(TurnEvalDefaults.ParaphraseGoldset, ParaphraseId)]);

        plan!.HoldoutItems.ShouldHaveSingleItem();
        plan.TrainItems.ShouldBeEmpty();
    }

    [Test]
    public async Task RecipeItems_AreNeverPlanned()
    {
        GivenRows(Row(HoldoutIds[0], null, false, chosenTool: Sharpened));
        GivenBaseGoldset(new TurnGoldsetItem
        {
            Id = HoldoutIds[0], Message = "Starte die Einrichtung", Locale = "de", ExpectedRecipe = "setup-consultation"
        });

        (await _gate.PlanAsync(Sharpened, [])).ShouldNotBeNull().HoldoutItems.ShouldBeEmpty();
    }

    [Test]
    public async Task TheReplay_UsesTheModelOfTheReferenceRun()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));
        GivenReplayChoosing(Sharpened, [Sharpened]);

        await _gate.ReplayAsync(PlanOf(HoldoutIds[0]));

        await _replayService.Received(1).ReplayAsync(
            Arg.Is<TurnGoldsetItem>(item => item.Id == HoldoutIds[0]), Model, Arg.Any<string>(),
            Arg.Any<List<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ChoosingTheExpectedTool_IsAHit()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));
        GivenReplayChoosing(Sharpened, [Sharpened, Confused]);

        var verdicts = await _gate.ReplayAsync(PlanOf(HoldoutIds[0]));

        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])].ShouldBe(true);
    }

    // A narrowed description can push the expected tool out of the offered list. SelectionHit would then be
    // null - but the user did not get the tool, so it is a miss, not "unmeasured".
    [Test]
    public async Task AnExpectedToolThatDroppedOutOfTheToolset_IsAMiss()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));
        GivenReplayChoosing(Confused, [Confused, Unrelated]);

        var verdicts = await _gate.ReplayAsync(PlanOf(HoldoutIds[0]));

        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])].ShouldBe(false);
    }

    [Test]
    public async Task AnUnansweredReplay_IsUnmeasured()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(new TurnReplayResult { Success = false, Error = "the provider timed out" });

        var verdicts = await _gate.ReplayAsync(PlanOf(HoldoutIds[0]));

        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])].ShouldBeNull();
    }

    // One replay that throws must not cost the answers of the others, nor end the gate pass.
    [Test]
    public async Task AReplayThatThrows_IsUnmeasuredAndTheOtherItemsStillRun()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened), Item(HoldoutIds[1], Sharpened));
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<TurnGoldsetItem>().Id == HoldoutIds[0]
                ? throw new HttpRequestException("the provider reset the connection")
                : new TurnReplayResult { Success = true, ChosenTool = Sharpened, AvailableToolNames = [Sharpened] });
        var plan = new GoldsetReplayPlan(
            RunId, Model, TurnEvalScorer.ScorerVersion,
            [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0]), new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[1])],
            []);

        var verdicts = await _gate.ReplayAsync(plan);

        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])].ShouldBeNull();
        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[1])].ShouldBe(true);
    }

    [Test]
    public async Task ACancelledRun_IsNotSwallowed()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened));
        using var cancellation = new CancellationTokenSource();
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns<TurnReplayResult>(_ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });

        await Should.ThrowAsync<OperationCanceledException>(
            () => _gate.ReplayAsync(PlanOf(HoldoutIds[0]), cancellation.Token));
    }

    // An HTTP client timeout surfaces as TaskCanceledException although nobody cancelled the run: that is one
    // unanswered item, not the end of the gate pass.
    [Test]
    public async Task ATimeoutOfOneReplay_IsUnmeasuredAndTheOtherItemsStillRun()
    {
        GivenBaseGoldset(Item(HoldoutIds[0], Sharpened), Item(HoldoutIds[1], Sharpened));
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<TurnGoldsetItem>().Id == HoldoutIds[0]
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")
                : new TurnReplayResult { Success = true, ChosenTool = Sharpened, AvailableToolNames = [Sharpened] });
        var plan = new GoldsetReplayPlan(
            RunId, Model, TurnEvalScorer.ScorerVersion,
            [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0]), new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[1])],
            []);

        var verdicts = await _gate.ReplayAsync(plan);

        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])].ShouldBeNull();
        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[1])].ShouldBe(true);
    }

    [Test]
    public async Task ANoToolItem_IsAHitWhenNoToolWasChosen()
    {
        GivenBaseGoldset(new TurnGoldsetItem { Id = HoldoutIds[0], Message = "Danke dir!", Locale = "de" });
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(new TurnReplayResult { Success = true, ChosenTool = null, AvailableToolNames = [Sharpened] });

        var verdicts = await _gate.ReplayAsync(PlanOf(HoldoutIds[0]));

        verdicts[new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, HoldoutIds[0])].ShouldBe(true);
    }

    private static GoldsetReplayPlan PlanOf(string holdoutId) =>
        new(RunId, Model, TurnEvalScorer.ScorerVersion, [new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, holdoutId)], []);

    private void GivenRows(params EvalRunItem[] rows) =>
        _evalRunItems.ListByRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(rows);

    private void GivenBaseGoldset(params TurnGoldsetItem[] items) =>
        _goldsetLoader.LoadAsync(TurnEvalDefaults.DefaultGoldset, Arg.Any<CancellationToken>()).Returns(items);

    private void GivenI18nGoldset(params TurnGoldsetItem[] items) =>
        _goldsetLoader.LoadAsync(TurnEvalDefaults.I18nGoldset, Arg.Any<CancellationToken>()).Returns(items);

    private void GivenI18nRun(params EvalRunItem[] rows)
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.I18nGoldset, TurnEvalScorer.ScorerVersion, Model, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = I18nRunId, Model = Model });
        _evalRunItems.ListByRunAsync(I18nRunId, Arg.Any<CancellationToken>()).Returns(rows);
    }

    private void GivenReplayChoosing(string tool, List<string> offered) =>
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(new TurnReplayResult { Success = true, ChosenTool = tool, AvailableToolNames = offered });

    private static EvalRunItem Row(
        string itemId, string? expectedTool, bool selectionHit, string? chosenTool = null, Guid? runId = null) => new()
    {
        Id = Guid.NewGuid(),
        EvalRunId = runId ?? RunId,
        ItemId = itemId,
        Locale = "de",
        ExpectedTool = expectedTool,
        ChosenTool = chosenTool ?? (selectionHit ? expectedTool : Unrelated),
        RetrievalHit = true,
        SelectionHit = selectionHit,
        Passed = selectionHit
    };

    private static TurnGoldsetItem Item(string itemId, string expectedTool) => new()
    {
        Id = itemId,
        Message = "Zeige mir etwas",
        Locale = "de",
        ExpectedTool = expectedTool
    };
}
