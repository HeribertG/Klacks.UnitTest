// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the follow-through guarantee of SkillToolsetGuaranteeResolver: after a turn that ran a KnowHow
/// (Explain) skill only the Act skills with an active ExplainedBy edge to it that are also fronted by an Advise
/// skill (an advisory chain), plus those Advise skills, stay in the toolset - an Explain skill whose Act skills
/// have no adviser guarantees nothing, because a KnowHow answer is often plain curiosity; after an Advise skill
/// its AdvisesFor targets stay.
/// The previous skill's class is its curated Effect, only permitted skills qualify, inactive edges are ignored,
/// the claim is capped and ordered deterministically, and no relation read happens unless the previous turn
/// contained an Explain or Advise skill.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetGuaranteeResolverFollowThroughTests
{
    private const string ExplainSkill = "explain_contract_setup";
    private const string AdviseSkill = "evaluate_contract_templates";
    private const string ActSkill = "create_contract_from_template";
    private const string UnrelatedMutateSkill = "create_contract";
    private const string UserMessage = "80 Prozent vom Firmenwert, monatlich";

    private ISkillRelationRepository _relations = null!;
    private SkillToolsetGuaranteeResolver _resolver = null!;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _agentId = Guid.NewGuid();
        _relations = Substitute.For<ISkillRelationRepository>();
        _relations.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(
                default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        var recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());

        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());

        _resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(),
            phrases,
            _relations,
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());
    }

    private static AgentSkill Skill(string name, SkillEffect effect, int sortOrder = 0) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = effect,
        SortOrder = sortOrder,
        TriggerKeywords = "[]"
    };

    private SkillRelation Edge(
        string from, string to, SkillRelationType type, SkillRelationStatus status = SkillRelationStatus.Active) => new()
    {
        AgentId = _agentId,
        SkillAName = from,
        SkillBName = to,
        Type = type,
        Status = status
    };

    private void GivenEdges(params SkillRelation[] edges) =>
        _relations.GetByAgentAsync(_agentId, Arg.Any<CancellationToken>()).Returns(edges.ToList());

    private static List<AgentSkill> ContractSkills() =>
    [
        Skill(ExplainSkill, SkillEffect.Explain),
        Skill(AdviseSkill, SkillEffect.Advise),
        Skill(ActSkill, SkillEffect.Mutate),
        Skill(UnrelatedMutateSkill, SkillEffect.Mutate)
    ];

    private SkillRelation[] ContractEdges() =>
    [
        Edge(ActSkill, ExplainSkill, SkillRelationType.ExplainedBy),
        Edge(AdviseSkill, ActSkill, SkillRelationType.AdvisesFor)
    ];

    private async Task<List<string>> Resolve(
        IReadOnlyList<AgentSkill> permitted, IReadOnlyList<string>? previous)
    {
        var request = new SkillToolsetGuaranteeRequest(
            _agentId, permitted, [], UserMessage, "conv", null, Guid.NewGuid().ToString(), "de", [],
            null, previous, null, true);

        var result = await _resolver.ResolveAsync(request, CancellationToken.None);

        return result.GuaranteedSkills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    [Test]
    public async Task PreviousExplainSkill_GuaranteesItsActSkillAndTheAdviseSkillInFrontOfIt()
    {
        GivenEdges(ContractEdges());

        var names = await Resolve(ContractSkills(), [ExplainSkill]);

        names.ShouldBe(new[] { ActSkill, AdviseSkill }.OrderBy(n => n, StringComparer.Ordinal));
        names.ShouldNotContain(UnrelatedMutateSkill);
    }

    [Test]
    public async Task PreviousAdviseSkill_GuaranteesItsAdvisesForTargets()
    {
        GivenEdges(ContractEdges());

        var names = await Resolve(ContractSkills(), [AdviseSkill]);

        names.ShouldBe(new[] { ActSkill });
    }

    [Test]
    public async Task NoPreviousTurn_GuaranteesNothing_AndReadsNoRelations()
    {
        GivenEdges(ContractEdges());

        (await Resolve(ContractSkills(), null)).ShouldBeEmpty();
        (await Resolve(ContractSkills(), [])).ShouldBeEmpty();

        await _relations.DidNotReceiveWithAnyArgs().GetByAgentAsync(default, default);
    }

    [Test]
    public async Task PreviousTurnWithoutExplainOrAdviseSkill_ReadsNoRelations()
    {
        GivenEdges(ContractEdges());

        var names = await Resolve(ContractSkills(), [UnrelatedMutateSkill, ActSkill]);

        names.ShouldBeEmpty();
        await _relations.DidNotReceiveWithAnyArgs().GetByAgentAsync(default, default);
    }

    [Test]
    public async Task ThePreviousSkillClass_IsItsCuratedEffect_NotItsName()
    {
        var skills = ContractSkills();
        skills[0] = Skill(ExplainSkill, SkillEffect.Read);
        GivenEdges(ContractEdges());

        var names = await Resolve(skills, [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task InactiveEdges_AreIgnored()
    {
        GivenEdges(
            Edge(ActSkill, ExplainSkill, SkillRelationType.ExplainedBy, SkillRelationStatus.Candidate),
            Edge(AdviseSkill, ActSkill, SkillRelationType.AdvisesFor, SkillRelationStatus.Retired));

        var names = await Resolve(ContractSkills(), [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task OtherEdgeTypes_AreNotFollowed()
    {
        GivenEdges(
            Edge(ActSkill, ExplainSkill, SkillRelationType.SeeAlso),
            Edge(ActSkill, ExplainSkill, SkillRelationType.CoRequired));

        var names = await Resolve(ContractSkills(), [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task UnpermittedSkills_AreExcluded()
    {
        GivenEdges(ContractEdges());
        var permitted = ContractSkills().Where(s => s.Name != ActSkill).ToList();

        var names = await Resolve(permitted, [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task UnpermittedPreviousSkill_GuaranteesNothing()
    {
        GivenEdges(ContractEdges());
        var permitted = ContractSkills().Where(s => s.Name != ExplainSkill).ToList();

        var names = await Resolve(permitted, [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task TheClaim_IsCapped_ActsWithAnAdviserFirst_ThenByName_EachFollowedByItsAdvisers()
    {
        var skills = new List<AgentSkill> { Skill(ExplainSkill, SkillEffect.Explain) };
        var edges = new List<SkillRelation>();
        for (var i = 0; i < 6; i++)
        {
            skills.Add(Skill($"act_{i}", SkillEffect.Mutate));
            edges.Add(Edge($"act_{i}", ExplainSkill, SkillRelationType.ExplainedBy));
        }

        skills.Add(Skill("adviser_b", SkillEffect.Advise));
        skills.Add(Skill("adviser_a", SkillEffect.Advise));
        edges.Add(Edge("adviser_b", "act_4", SkillRelationType.AdvisesFor));
        edges.Add(Edge("adviser_a", "act_4", SkillRelationType.AdvisesFor));
        GivenEdges([.. edges]);

        var names = await Resolve(skills, [ExplainSkill]);

        names.Count.ShouldBe(SkillFollowThroughDefaults.MaxGuaranteedSkills);
        names.ShouldBe(new[] { "act_4", "adviser_a", "adviser_b" });
    }

    [Test]
    public async Task GroupGraphShape_FiveActs_TwoAdvisersOnOne_KeepsThatActAndItsAdvisers()
    {
        const string groupExplain = "explain_group_model";
        var skills = new List<AgentSkill>
        {
            Skill(groupExplain, SkillEffect.Explain),
            Skill("add_client_to_group", SkillEffect.Mutate),
            Skill("create_group", SkillEffect.Mutate),
            Skill("delete_group", SkillEffect.Mutate),
            Skill("move_group", SkillEffect.Mutate),
            Skill("update_group", SkillEffect.Mutate),
            Skill("evaluate_grouping_by_qualification", SkillEffect.Advise),
            Skill("evaluate_location_group_candidates", SkillEffect.Advise)
        };
        GivenEdges(
            Edge("add_client_to_group", groupExplain, SkillRelationType.ExplainedBy),
            Edge("create_group", groupExplain, SkillRelationType.ExplainedBy),
            Edge("delete_group", groupExplain, SkillRelationType.ExplainedBy),
            Edge("move_group", groupExplain, SkillRelationType.ExplainedBy),
            Edge("update_group", groupExplain, SkillRelationType.ExplainedBy),
            Edge("evaluate_grouping_by_qualification", "create_group", SkillRelationType.AdvisesFor),
            Edge("evaluate_location_group_candidates", "create_group", SkillRelationType.AdvisesFor));

        var names = await Resolve(skills, [groupExplain]);

        names.ShouldContain("create_group");
        names.ShouldContain("evaluate_grouping_by_qualification");
        names.ShouldContain("evaluate_location_group_candidates");
        names.Count.ShouldBe(SkillFollowThroughDefaults.MaxGuaranteedSkills);
    }

    [Test]
    public async Task AnAdviser_IsNeverKeptWithoutItsAct()
    {
        var skills = ContractSkills();
        GivenEdges(
            Edge(ActSkill, ExplainSkill, SkillRelationType.ExplainedBy),
            Edge(AdviseSkill, UnrelatedMutateSkill, SkillRelationType.AdvisesFor));

        var names = await Resolve(skills, [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task PreviousExplainSkill_WhoseActsHaveNoAdviser_GuaranteesNothing()
    {
        const string contractsExplain = "explain_contracts";
        var skills = new List<AgentSkill>
        {
            Skill(contractsExplain, SkillEffect.Explain),
            Skill("update_contract", SkillEffect.Mutate),
            Skill("get_contract_details", SkillEffect.Read),
            Skill("delete_contract", SkillEffect.Mutate)
        };
        GivenEdges(
            Edge("update_contract", contractsExplain, SkillRelationType.ExplainedBy),
            Edge("get_contract_details", contractsExplain, SkillRelationType.ExplainedBy),
            Edge("delete_contract", contractsExplain, SkillRelationType.ExplainedBy));

        var names = await Resolve(skills, [contractsExplain]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task PreviousExplainSkill_WithMixedActs_GuaranteesOnlyTheAdvisedActAndItsAdvisers()
    {
        var skills = ContractSkills();
        skills.Add(Skill("update_contract", SkillEffect.Mutate));
        GivenEdges(
            Edge(ActSkill, ExplainSkill, SkillRelationType.ExplainedBy),
            Edge("update_contract", ExplainSkill, SkillRelationType.ExplainedBy),
            Edge(UnrelatedMutateSkill, ExplainSkill, SkillRelationType.ExplainedBy),
            Edge(AdviseSkill, ActSkill, SkillRelationType.AdvisesFor));

        var names = await Resolve(skills, [ExplainSkill]);

        names.ShouldBe(new[] { ActSkill, AdviseSkill }.OrderBy(n => n, StringComparer.Ordinal));
        names.ShouldNotContain("update_contract");
        names.ShouldNotContain(UnrelatedMutateSkill);
    }

    [Test]
    public async Task PreviousExplainSkill_WhoseOnlyAdviserIsUnpermitted_GuaranteesNothing()
    {
        GivenEdges(ContractEdges());
        var permitted = ContractSkills().Where(s => s.Name != AdviseSkill).ToList();

        var names = await Resolve(permitted, [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task PreviousExplainSkill_WhoseOnlyAdviserEdgeIsInactive_GuaranteesNothing()
    {
        GivenEdges(
            Edge(ActSkill, ExplainSkill, SkillRelationType.ExplainedBy),
            Edge(AdviseSkill, ActSkill, SkillRelationType.AdvisesFor, SkillRelationStatus.Candidate));

        var names = await Resolve(ContractSkills(), [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task PreviousAdviseSkill_StillGuaranteesTargetsWithoutTheirOwnAdviser()
    {
        GivenEdges(Edge(AdviseSkill, ActSkill, SkillRelationType.AdvisesFor));

        var names = await Resolve(ContractSkills(), [AdviseSkill]);

        names.ShouldBe(new[] { ActSkill });
    }

    [Test]
    public async Task APreviousTurnWithBothExplainAndAdvise_UnionsBothChains()
    {
        GivenEdges(ContractEdges());

        var names = await Resolve(ContractSkills(), [ExplainSkill, AdviseSkill]);

        names.ShouldBe(new[] { ActSkill, AdviseSkill }.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Test]
    public async Task ARelationReadFailure_DegradesToNoGuarantee()
    {
        _relations.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<List<SkillRelation>>(_ => throw new InvalidOperationException("db down"));

        var names = await Resolve(ContractSkills(), [ExplainSkill]);

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task TheGuaranteedSkills_AreRecordedAsFollowThrough()
    {
        GivenEdges(ContractEdges());
        var request = new SkillToolsetGuaranteeRequest(
            _agentId, ContractSkills(), [], UserMessage, "conv", null, Guid.NewGuid().ToString(), "de", [],
            null, [ExplainSkill], null, true);

        var result = await _resolver.ResolveAsync(request, CancellationToken.None);

        result.GuaranteedSources[ActSkill].ShouldBe(ToolsetSkillSource.FollowThrough);
        result.GuaranteedSources[AdviseSkill].ShouldBe(ToolsetSkillSource.FollowThrough);
    }
}
