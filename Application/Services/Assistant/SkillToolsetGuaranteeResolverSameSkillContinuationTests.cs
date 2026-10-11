// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the same-skill continuation of SkillToolsetGuaranteeResolver: the Mutate skill the immediately
/// preceding turn last ran successfully stays in the next turn's toolset, whatever the follow-up's wording. The
/// continuation names arrive already resolved (a confirm_pending_action call named by the skill it replayed, see
/// AssistantLastAction.ContinuationSkillNames), ordered most recent last, and are separate from the follow-through's
/// previous-turn names. The class is the curated Effect, only permitted non-always-on skills qualify, the claim is
/// capped at one (the most recent), its source is FollowThrough and it ranks lowest at truncation.
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
public class SkillToolsetGuaranteeResolverSameSkillContinuationTests
{
    private const string UpdateContractSkill = "update_contract";
    private const string AddClientToGroupSkill = "add_client_to_group";
    private const string ListContractsSkill = "list_contracts";
    private const string ExplainContractSkill = "explain_contract_setup";
    private const string AdviseContractSkill = "evaluate_contract_templates";
    private const string AlwaysOnMutateSkill = "create_employee";
    private const string UserMessage = "Und stell bei dem Vertrag den Feiertagskalender auf Zürich um.";

    private SkillToolsetGuaranteeResolver _resolver = null!;
    private Guid _agentId;

    [SetUp]
    public void SetUp()
    {
        _agentId = Guid.NewGuid();
        var relations = Substitute.For<ISkillRelationRepository>();
        relations.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

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
            relations,
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());
    }

    private static AgentSkill Skill(string name, SkillEffect effect, bool alwaysOn = false, string triggerKeywords = "[]") => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = effect,
        AlwaysOn = alwaysOn,
        TriggerKeywords = triggerKeywords
    };

    private static List<AgentSkill> Skills() =>
    [
        Skill(AddClientToGroupSkill, SkillEffect.Mutate),
        Skill(UpdateContractSkill, SkillEffect.Mutate),
        Skill(ListContractsSkill, SkillEffect.Read),
        Skill(ExplainContractSkill, SkillEffect.Explain),
        Skill(AdviseContractSkill, SkillEffect.Advise),
        Skill(AlwaysOnMutateSkill, SkillEffect.Mutate, alwaysOn: true)
    ];

    private async Task<SkillToolsetGuaranteeResult> Resolve(
        IReadOnlyList<AgentSkill> permitted, IReadOnlyList<string>? continuation, IReadOnlyList<string>? previous = null)
    {
        var request = new SkillToolsetGuaranteeRequest(
            _agentId, permitted, [], UserMessage, "conv", null, Guid.NewGuid().ToString(), "de", [],
            null, previous, continuation, true);

        return await _resolver.ResolveAsync(request, CancellationToken.None);
    }

    private static List<string> Names(SkillToolsetGuaranteeResult result) =>
        result.GuaranteedSkills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Test]
    public async Task APreviousMutateSkill_IsReOffered_WithTheFollowThroughSource()
    {
        var result = await Resolve(Skills(), [UpdateContractSkill]);

        Names(result).ShouldBe([UpdateContractSkill]);
        result.GuaranteedSources[UpdateContractSkill].ShouldBe(ToolsetSkillSource.FollowThrough);
        result.LowestRankedSkillNames.ShouldBe([UpdateContractSkill]);
    }

    [Test]
    public async Task AMutateSkillOnlyAmongTheFollowThroughNames_IsNotContinued()
    {
        var result = await Resolve(Skills(), continuation: null, previous: [UpdateContractSkill]);

        Names(result).ShouldBeEmpty();
        result.LowestRankedSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task ASkillReplayedByConfirmPendingAction_IsReOffered()
    {
        var lastAction = new AssistantLastAction
        {
            Calls =
            [
                new AssistantLastActionCall
                {
                    SkillName = AutonomyDefaults.ConfirmPendingActionSkillName,
                    ReplayedSkillName = UpdateContractSkill,
                    Success = true
                }
            ]
        };

        var result = await Resolve(Skills(), lastAction.ContinuationSkillNames());

        Names(result).ShouldBe([UpdateContractSkill]);
    }

    [Test]
    public async Task AConfirmPendingActionWithoutItsReplayedName_ReOffersNothing()
    {
        var skills = Skills();
        skills.Add(Skill(AutonomyDefaults.ConfirmPendingActionSkillName, SkillEffect.Mutate));

        var result = await Resolve(skills, [AutonomyDefaults.ConfirmPendingActionSkillName]);

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task AFailedCall_IsNotReOffered()
    {
        var lastAction = new AssistantLastAction
        {
            Calls = [new AssistantLastActionCall { SkillName = UpdateContractSkill, Success = false }]
        };

        var result = await Resolve(Skills(), lastAction.ContinuationSkillNames());

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task ReadExplainAndAdviseSkills_AreNotReOfferedByThisRule()
    {
        var result = await Resolve(Skills(), [ListContractsSkill, ExplainContractSkill, AdviseContractSkill]);

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task TheClass_IsTheCuratedEffect_NotTheName()
    {
        var skills = Skills();
        skills[1] = Skill(UpdateContractSkill, SkillEffect.Read);

        var result = await Resolve(skills, [UpdateContractSkill]);

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task ASkillThatIsNotPermittedOrNotEnabled_IsNotReOffered()
    {
        var withoutUpdateContract = Skills().Where(s => s.Name != UpdateContractSkill).ToList();

        var result = await Resolve(withoutUpdateContract, [UpdateContractSkill]);

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task TheCap_KeepsOnlyTheMostRecentMutateSkill()
    {
        var result = await Resolve(Skills(), [AddClientToGroupSkill, UpdateContractSkill, ListContractsSkill]);

        Names(result).ShouldBe([UpdateContractSkill]);
        SkillFollowThroughDefaults.MaxContinuationSkills.ShouldBe(1);
    }

    [Test]
    public async Task MostRecent_FollowsTheLastOccurrenceOfARepeatedSkill()
    {
        var lastAction = new AssistantLastAction
        {
            Calls =
            [
                new AssistantLastActionCall { SkillName = AddClientToGroupSkill, Success = true },
                new AssistantLastActionCall { SkillName = UpdateContractSkill, Success = true },
                new AssistantLastActionCall { SkillName = AddClientToGroupSkill, Success = true }
            ]
        };

        var result = await Resolve(Skills(), lastAction.ContinuationSkillNames());

        Names(result).ShouldBe([AddClientToGroupSkill]);
    }

    [Test]
    public async Task AnAlwaysOnMutateSkill_IsSkipped_InFavourOfTheNextMostRecent()
    {
        var result = await Resolve(Skills(), [UpdateContractSkill, AlwaysOnMutateSkill]);

        Names(result).ShouldBe([UpdateContractSkill]);
    }

    [Test]
    public async Task ASkillAlreadyGuaranteedByAStrongerSource_KeepsThatSource_AndIsNotDuplicated()
    {
        var skills = Skills();
        skills[1] = Skill(UpdateContractSkill, SkillEffect.Mutate, triggerKeywords: "[\"Feiertagskalender\"]");

        var result = await Resolve(skills, [UpdateContractSkill]);

        result.GuaranteedSkills.Count(s => s.Name == UpdateContractSkill).ShouldBe(1);
        result.GuaranteedSources[UpdateContractSkill].ShouldBe(ToolsetSkillSource.Keyword);
        result.LowestRankedSkillNames.ShouldBeEmpty();
    }

    [Test]
    public async Task NoPreviousTurn_ReOffersNothing()
    {
        Names(await Resolve(Skills(), null)).ShouldBeEmpty();
        Names(await Resolve(Skills(), [])).ShouldBeEmpty();
    }
}
