// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the second source of description proposals: the pure selection misses of the latest full
/// eval run. Four rules keep it honest. Only the train partition may feed it - a proposal built from a
/// holdout item would be judged by the very item it came from, and the ids are handed to the store so
/// the limit cannot be filled with rows the loop may never spend. Only selection misses count - a
/// tighter description cannot fix a tool that was never in the toolset. Every consumed item is
/// watermarked, so the same misses cannot justify a fresh narrowing on every run. And the number of
/// groups per run is capped: every group is one paid model call plus one pending row, and an uncapped
/// goldset branch would fill every sharpener slot ahead of the user corrections.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation;

using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.UnitTest.Application.Services.Assistant.Learning;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillDescriptionOptimizerGoldsetSourceTests
{
    private const string WronglyChosen = "list_clients";
    private const string OtherWronglyChosen = "list_absences";
    private const string IntendedTarget = "revenue_per_client";
    private const string Suggestion =
        "{\"description\":\"Lists the contract data of one client.\",\"justification\":\"too broad\"}";

    private const string ParaphraseItemId = "para-ts-001-1";
    private const string ResolvedModelId = "resolved-model";
    private const string ResolvedApiModelId = "resolved-model-api-id";
    private const string ReferenceModel = "deepseek-flash";
    private const string OtherModel = "deepseek-v4-pro";

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid ParaphraseRunId = Guid.NewGuid();
    private static readonly Guid I18nRunId = Guid.NewGuid();

    private static readonly string[] TranslatedLocales = ["ja", "ar", "zh-CN", "fr", "th", "vi", "ko", "pl"];

    private ISkillSelectionTrajectoryRepository _trajectories = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private IAgentRepository _agents = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private IEvalRunRepository _evalRuns = null!;
    private IEvalRunItemRepository _evalRunItems = null!;
    private ITurnGoldsetLoader _goldsetLoader = null!;
    private ISkillLearningOptionsProvider _learningOptions = null!;
    private FakeLLMProvider _provider = null!;
    private LLMModel _model = null!;
    private ICheapestModelResolver _modelResolver = null!;
    private SkillDescriptionOptimizer _optimizer = null!;
    private Agent _agent = null!;
    private AgentSkill _skill = null!;
    private List<ProposedSkillChange> _added = null!;

    // Deterministic partition members of the shipped goldset, taken from GoldsetPartitioner so the
    // test cannot drift away from the implementation.
    private static string TrainItemId =>
        Enumerable.Range(1, 500).Select(i => $"ts-{i:D3}")
            .First(id => GoldsetPartitioner.IsTrain(id));

    private static string HoldoutItemId =>
        Enumerable.Range(1, 500).Select(i => $"ts-{i:D3}")
            .First(id => GoldsetPartitioner.IsHoldout(id));

    private static IReadOnlyList<string> TrainItemIds(int count) =>
        [.. Enumerable.Range(1, 500).Select(i => $"ts-{i:D3}").Where(GoldsetPartitioner.IsTrain).Take(count)];

    [SetUp]
    public void SetUp()
    {
        _added = [];
        _agent = new Agent { Id = Guid.NewGuid() };
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);

        _skill = new AgentSkill
        {
            Id = Guid.NewGuid(),
            AgentId = _agent.Id,
            Name = WronglyChosen,
            Description = "Lists everything about clients."
        };

        _skills = Substitute.For<IAgentSkillRepository>();
        _skills.GetByNameAsync(_agent.Id, WronglyChosen, Arg.Any<CancellationToken>()).Returns(_skill);

        _proposals = Substitute.For<IProposedSkillChangeRepository>();
        _proposals.HasOpenProposalForSkillAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _proposals
            .When(x => x.AddAsync(Arg.Any<ProposedSkillChange>(), Arg.Any<CancellationToken>()))
            .Do(call => _added.Add(call.Arg<ProposedSkillChange>()));

        _trajectories = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _trajectories.GetUncorrectedWrongSkillAsync(
                _agent.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _cases = Substitute.For<ISkillLearningCaseRepository>();
        _goldenCases = Substitute.For<ISkillLearningGoldenCaseRepository>();
        _goldenCases.ExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _learningOptions = Substitute.For<ISkillLearningOptionsProvider>();
        _learningOptions.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new SkillLearningOptions(
                SkillLearningDefaults.MinOccurrences,
                SkillLearningDefaults.MinDistinctUsers,
                SkillLearningDefaults.PruneDays,
                SkillLearningDefaults.RetentionDays,
                ReferenceModel: ReferenceModel));

        _evalRuns = Substitute.For<IEvalRunRepository>();
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, ReferenceModel, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = RunId, Model = ReferenceModel });

        _evalRunItems = Substitute.For<IEvalRunItemRepository>();
        _evalRunItems.ListUnconsumedSelectionMissesAsync(
                RunId,
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        _goldsetLoader = Substitute.For<ITurnGoldsetLoader>();
        _goldsetLoader.LoadAsync(TurnEvalDefaults.DefaultGoldset, Arg.Any<CancellationToken>())
            .Returns([
                Item(TrainItemId),
                Item(HoldoutItemId)
            ]);

        _provider = new FakeLLMProvider();
        _provider.Answering(Suggestion);

        _model = new LLMModel { ModelId = ResolvedModelId, ApiModelId = ResolvedApiModelId };
        _modelResolver = Substitute.For<ICheapestModelResolver>();
        _modelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)_model, (ILLMProvider?)_provider));

        _optimizer = new SkillDescriptionOptimizer(
            _trajectories, _proposals, _skills, _agents, _cases, _goldenCases,
            _evalRuns, _evalRunItems, _goldsetLoader, _modelResolver, _learningOptions,
            NullLogger<SkillDescriptionOptimizer>.Instance);
    }

    [Test]
    public async Task ASelectionMissOfTheTrainPartition_BecomesAGoldsetBornProposal()
    {
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(1);
        _added.Count.ShouldBe(1);
        _added[0].SkillId.ShouldBe(_skill.Id);
        _added[0].Origin.ShouldBe(ProposedChangeOrigins.GoldsetEval);
        _added[0].Status.ShouldBe(ProposedChangeStatuses.Pending);
        _added[0].Field.ShouldBe(ProposedChangeFields.Description);
    }

    // A proposal built from a holdout item would be judged by the very item it came from.
    [Test]
    public async Task ASelectionMissOfTheHoldoutPartition_IsIgnored()
    {
        GivenMisses(Miss(HoldoutItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(0);
        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task TheConsumedItems_AreWatermarked()
    {
        var miss = Miss(TrainItemId);
        GivenMisses(miss);

        await _optimizer.GenerateProposalsAsync(30);

        await _evalRunItems.Received(1).MarkConsumedAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == miss.Id),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutAFullRun_NothingIsProposedFromTheGoldset()
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, ReferenceModel, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(0);
        _added.ShouldBeEmpty();
    }

    // The dev database also carries nightly full runs of other models. The learning loop must not spend a
    // goldset miss group generated against one of those - a run of another model is treated exactly like no
    // reference run at all.
    [Test]
    public async Task AFullRunOfAnotherModel_IsTreatedAsNoReferenceRun()
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, ReferenceModel, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, OtherModel, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = Guid.NewGuid(), Model = OtherModel });
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(0);
        _added.ShouldBeEmpty();
    }

    // Neither KLACKSY_LEARNING_REFERENCE_MODEL nor a database default model resolved to anything: the
    // optimizer must not guess a model to query eval_runs with, it has to skip the goldset source entirely.
    [Test]
    public async Task WithoutAResolvedReferenceModel_NoRunIsQueriedAndNothingIsProposed()
    {
        _learningOptions.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new SkillLearningOptions(
                SkillLearningDefaults.MinOccurrences,
                SkillLearningDefaults.MinDistinctUsers,
                SkillLearningDefaults.PruneDays,
                SkillLearningDefaults.RetentionDays,
                ReferenceModel: null));
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _evalRuns.DidNotReceive().GetLatestFullRunAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASkillThatAlreadyCarriesAnOpenProposal_IsLeftAlone()
    {
        _proposals.HasOpenProposalForSkillAsync(
                _skill.Id, ProposedChangeFields.Description, Arg.Any<CancellationToken>())
            .Returns(true);
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _evalRunItems.DidNotReceive().MarkConsumedAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // The expectation is already a golden case - the seeder wrote it. Freezing it again would create a
    // duplicate row for every proposal.
    [Test]
    public async Task AGoldsetBornProposal_FreezesNoAdditionalGoldenCase()
    {
        GivenMisses(Miss(TrainItemId));

        await _optimizer.GenerateProposalsAsync(30);

        await _goldenCases.DidNotReceive().AddAsync(
            Arg.Any<SkillLearningGoldenCase>(), Arg.Any<CancellationToken>());
    }

    // The store is asked for the train ids of the goldset, not for whatever the limit happens to reach.
    // Before this the newest 200 rows were taken ordered by item id and filtered afterwards, so a window
    // full of holdout ids or of skipped groups starved the branch for good.
    [Test]
    public async Task OnlyTheTrainItemIdsOfTheGoldset_AreAskedForFromTheStore()
    {
        GivenMisses(Miss(TrainItemId));

        await _optimizer.GenerateProposalsAsync(30);

        await _evalRunItems.Received(1).ListUnconsumedSelectionMissesAsync(
            RunId,
            Arg.Is<IReadOnlyCollection<string>>(ids =>
                ids.Contains(TrainItemId) && !ids.Contains(HoldoutItemId)),
            SkillLearningDefaults.MaxGoldsetMissesPerRun,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AHoldoutMissThatSortsFirst_NeverReachesTheGroupBuilder()
    {
        GivenSkill(OtherWronglyChosen);
        GivenMisses(Miss(HoldoutItemId, OtherWronglyChosen), Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(1);
        _added.ShouldHaveSingleItem().SkillName.ShouldBe(WronglyChosen);
    }

    // Every group is one paid model call and one pending row. Uncapped, the goldset branch alone could
    // open more proposals in one run than the sharpener may ever decide on.
    [Test]
    public async Task MoreGroupsThanTheCap_ProduceExactlyTheCapManyProposals()
    {
        var ids = TrainItemIds(4);
        string[] skills = ["skill_a", "skill_b", "skill_c", "skill_d"];
        GivenGoldsetItems([.. ids]);
        foreach (var name in skills)
        {
            GivenSkill(name);
        }

        _provider.Answering(Suggestion, Suggestion, Suggestion);
        GivenMisses([.. ids.Select((id, index) => Miss(id, skills[index]))]);

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(SkillLearningDefaults.MaxGoldsetProposalsPerRun);
        _added.Count.ShouldBe(SkillLearningDefaults.MaxGoldsetProposalsPerRun);
    }

    // The cap keeps the groups the eval saw most evidence for, not the ones whose skill name sorts first.
    [Test]
    public async Task TheCapKeepsTheGroupsWithTheMostEvidence()
    {
        var ids = TrainItemIds(5);
        string[] skills = ["skill_a", "skill_b", "skill_c", "skill_d"];
        GivenGoldsetItems([.. ids]);
        foreach (var name in skills)
        {
            GivenSkill(name);
        }

        _provider.Answering(Suggestion, Suggestion, Suggestion);
        GivenMisses(
            Miss(ids[0], skills[0]),
            Miss(ids[1], skills[1]),
            Miss(ids[2], skills[2]),
            Miss(ids[3], skills[3]),
            Miss(ids[4], skills[3]));

        await _optimizer.GenerateProposalsAsync(30);

        _added.Select(p => p.SkillName).ShouldContain(skills[3]);
        _added.Select(p => p.SkillName).ShouldNotContain(skills[2]);
    }

    // One German miss translated into eight languages is one piece of evidence, not eight: the cap ranks the
    // groups by distinct source items (translations and paraphrases count for their source).
    [Test]
    public async Task TheCapRanksByDistinctSourceItems_NotByRowCount()
    {
        var ids = TrainItemIds(7);
        string[] skills = ["skill_a", "skill_b", "skill_c", "skill_d"];
        GivenGoldsetItems([.. ids]);
        foreach (var name in skills)
        {
            GivenSkill(name);
        }

        _provider.Answering(Suggestion, Suggestion, Suggestion);
        GivenMisses(
            Miss(ids[0], skills[0]), Miss(ids[1], skills[0]),
            Miss(ids[2], skills[1]), Miss(ids[3], skills[1]),
            Miss(ids[4], skills[2]), Miss(ids[5], skills[2]));
        GivenI18nRun([.. TranslatedLocales.Select(locale => Miss(GoldsetTranslationId.Compose(locale, ids[6]), skills[3]))]);

        await _optimizer.GenerateProposalsAsync(30);

        _added.Select(p => p.SkillName).ShouldBe([skills[0], skills[1], skills[2]], ignoreOrder: true);
    }

    // The evidence is capped at a handful of misses; taken in row order it would always be the default
    // goldset's, so a translation miss could never reach the optimizer or the gate's train replay.
    [Test]
    public async Task TheEvidence_IsSpreadOverSourcesAndGoldsets()
    {
        var ids = TrainItemIds(7);
        GivenGoldsetItems([.. ids]);
        GivenMisses([.. ids.Take(6).Select(id => Miss(id))]);
        var translated = GoldsetTranslationId.Compose(TranslatedLocales[0], ids[6]);
        GivenI18nRun(Miss(translated), Miss(GoldsetTranslationId.Compose(TranslatedLocales[1], ids[6])));

        await _optimizer.GenerateProposalsAsync(30);

        var evidence = GoldsetMissEvidenceCodec.Parse(_added.ShouldHaveSingleItem().EvidenceJson).Items;
        evidence.ShouldContain(item => item.Goldset == TurnEvalDefaults.I18nGoldset);
        evidence.Count(item => item.Goldset == TurnEvalDefaults.I18nGoldset).ShouldBe(1);
    }

    private void GivenI18nRun(params EvalRunItem[] misses)
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.I18nGoldset, TurnEvalScorer.ScorerVersion, ReferenceModel, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = I18nRunId, Model = ReferenceModel });
        _goldsetLoader.LoadAsync(TurnEvalDefaults.I18nGoldset, Arg.Any<CancellationToken>())
            .Returns([.. misses.Select(miss => Item(miss.ItemId))]);
        _evalRunItems.ListUnconsumedSelectionMissesAsync(
                I18nRunId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(misses);
    }

    private void GivenParaphraseRun(params EvalRunItem[] misses)
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.ParaphraseGoldset, TurnEvalScorer.ScorerVersion, ReferenceModel, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = ParaphraseRunId, Model = ReferenceModel });
        _goldsetLoader.LoadAsync(TurnEvalDefaults.ParaphraseGoldset, Arg.Any<CancellationToken>())
            .Returns([Item(ParaphraseItemId)]);
        _evalRunItems.ListUnconsumedSelectionMissesAsync(
                ParaphraseRunId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(misses);
    }

    // The gate replays exactly the misses a proposal was built from, so they have to travel with it.
    [Test]
    public async Task AGoldsetBornProposal_CarriesTheEvalItemsItWasBuiltFrom()
    {
        GivenMisses(Miss(TrainItemId));

        await _optimizer.GenerateProposalsAsync(30);

        GoldsetMissEvidenceCodec.Parse(_added.ShouldHaveSingleItem().EvidenceJson).Items
            .ShouldHaveSingleItem().ShouldBe(new GoldsetItemRef(TurnEvalDefaults.DefaultGoldset, TrainItemId));
    }

    [Test]
    public async Task MissesOfTheParaphraseGoldset_AlsoOpenAProposal()
    {
        var miss = Miss(ParaphraseItemId);
        GivenParaphraseRun(miss);

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.Generated.ShouldBe(1);
        GoldsetMissEvidenceCodec.Parse(_added.ShouldHaveSingleItem().EvidenceJson).Items
            .ShouldHaveSingleItem().ShouldBe(new GoldsetItemRef(TurnEvalDefaults.ParaphraseGoldset, ParaphraseItemId));
        await _evalRunItems.Received(1).MarkConsumedAsync(
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == miss.Id),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissesOfBothGoldsetsForOneSkill_OpenOneProposal()
    {
        GivenMisses(Miss(TrainItemId));
        GivenParaphraseRun(Miss(ParaphraseItemId));

        await _optimizer.GenerateProposalsAsync(30);

        GoldsetMissEvidenceCodec.Parse(_added.ShouldHaveSingleItem().EvidenceJson).Items.Count.ShouldBe(2);
    }

    [Test]
    public async Task AnUnreadableParaphraseGoldset_DoesNotStopTheDefaultGoldset()
    {
        GivenParaphraseRun();
        _goldsetLoader.LoadAsync(TurnEvalDefaults.ParaphraseGoldset, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("not generated yet"));
        GivenMisses(Miss(TrainItemId));

        (await _optimizer.GenerateProposalsAsync(30)).Generated.ShouldBe(1);
    }

    // Ordering every enabled model by cost picked an unpriced model on an unpriced catalogue, whose answers
    // were unusable, so a run with three goldset groups produced no proposal at all. The model choice is the
    // shared resolver's, which ignores unpriced models.
    [Test]
    public async Task TheSuggestion_IsRequestedFromTheModelTheSharedResolverPicks()
    {
        GivenMisses(Miss(TrainItemId));

        await _optimizer.GenerateProposalsAsync(30);

        await _modelResolver.Received(1).ResolveAsync(Arg.Any<CancellationToken>());
        _provider.Requests.ShouldHaveSingleItem().ModelId.ShouldBe(ResolvedApiModelId);
    }

    [Test]
    public async Task WithoutAResolvedModel_NothingIsProposedAndNothingIsSpent()
    {
        _modelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)null, (ILLMProvider?)null));
        GivenMisses(Miss(TrainItemId));

        (await _optimizer.GenerateProposalsAsync(30)).Generated.ShouldBe(0);

        _added.ShouldBeEmpty();
        await _evalRunItems.DidNotReceive().MarkConsumedAsync(
            Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private void GivenMisses(params EvalRunItem[] misses) =>
        _evalRunItems.ListUnconsumedSelectionMissesAsync(
                RunId,
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(misses);

    private void GivenGoldsetItems(params string[] itemIds) =>
        _goldsetLoader.LoadAsync(TurnEvalDefaults.DefaultGoldset, Arg.Any<CancellationToken>())
            .Returns([.. itemIds.Select(Item)]);

    private AgentSkill GivenSkill(string name)
    {
        var skill = new AgentSkill
        {
            Id = Guid.NewGuid(),
            AgentId = _agent.Id,
            Name = name,
            Description = $"Lists everything about {name}."
        };

        _skills.GetByNameAsync(_agent.Id, name, Arg.Any<CancellationToken>()).Returns(skill);
        return skill;
    }

    private static EvalRunItem Miss(string itemId, string chosenTool = WronglyChosen) => new()
    {
        Id = Guid.NewGuid(),
        EvalRunId = RunId,
        ItemId = itemId,
        Locale = "de",
        ExpectedTool = IntendedTarget,
        ChosenTool = chosenTool,
        RetrievalHit = true,
        SelectionHit = false,
        Passed = false
    };

    private static TurnGoldsetItem Item(string itemId) => new()
    {
        Id = itemId,
        Message = "Zeige mir die Umsatzstatistik pro Kunde",
        Locale = "de",
        ExpectedTool = IntendedTarget
    };
}
