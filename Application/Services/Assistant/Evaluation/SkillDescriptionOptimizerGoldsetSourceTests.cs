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

    private static readonly Guid RunId = Guid.NewGuid();

    private ISkillSelectionTrajectoryRepository _trajectories = null!;
    private IProposedSkillChangeRepository _proposals = null!;
    private IAgentSkillRepository _skills = null!;
    private IAgentRepository _agents = null!;
    private ISkillLearningCaseRepository _cases = null!;
    private ISkillLearningGoldenCaseRepository _goldenCases = null!;
    private IEvalRunRepository _evalRuns = null!;
    private IEvalRunItemRepository _evalRunItems = null!;
    private ITurnGoldsetLoader _goldsetLoader = null!;
    private FakeLLMProvider _provider = null!;
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

        _evalRuns = Substitute.For<IEvalRunRepository>();
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = RunId, Model = "deepseek-v4-pro" });

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

        var factory = Substitute.For<ILLMProviderFactory>();
        factory.GetProviderForModelAsync(Arg.Any<string>()).Returns(_provider);

        var llm = Substitute.For<ILLMRepository>();
        llm.GetModelsAsync(true).Returns([new LLMModel { ModelId = "fake", ApiModelId = "fake-1" }]);

        _optimizer = new SkillDescriptionOptimizer(
            _trajectories, _proposals, _skills, _agents, _cases, _goldenCases,
            _evalRuns, _evalRunItems, _goldsetLoader, factory, llm,
            NullLogger<SkillDescriptionOptimizer>.Instance);
    }

    [Test]
    public async Task ASelectionMissOfTheTrainPartition_BecomesAGoldsetBornProposal()
    {
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.ShouldBe(1);
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

        generated.ShouldBe(0);
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
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.ShouldBe(0);
        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task ASkillThatAlreadyCarriesAnOpenProposal_IsLeftAlone()
    {
        _proposals.HasOpenProposalForSkillAsync(
                _skill.Id, ProposedChangeFields.Description, Arg.Any<CancellationToken>())
            .Returns(true);
        GivenMisses(Miss(TrainItemId));

        var generated = await _optimizer.GenerateProposalsAsync(30);

        generated.ShouldBe(0);
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

        generated.ShouldBe(1);
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

        generated.ShouldBe(SkillLearningDefaults.MaxGoldsetProposalsPerRun);
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
