// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for the message-level guarantees of SkillToolsetGuaranteeResolver.ResolveAsync that no other
/// test pins directly: the turn-intent Explain skills and veto, the keyword guarantee (never for always-on skills), the
/// create_shift/cut_shift pair, the plan candidate, the pending-notes guarantee with its gates and the clarification pins,
/// plus the rule that a stronger source wins whatever order the layers claim a skill in.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.TurnIntent;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetGuaranteeResolverCharacterizationTests
{
    private const string NeutralMessage = "Danke, das passt so.";
    private const string KeywordSkill = "update_contract";
    private const string ExplainSkill = "explain_contracts";
    private const string PinnedSkill = "add_client_to_group";
    private const string PendingNotesSkill = "manage_pending_notes";
    private const string CreateShiftSkill = "create_shift";
    private const string CutShiftSkill = "cut_shift";
    private const string TwoMutationsMessage = "Erstelle den Mitarbeiter Hans Muster. Lösche den Mitarbeiter Peter Meier.";
    private const string OneMutationMessage = "Erstelle den Mitarbeiter Hans Muster.";

    private IPendingUserNoteRepository _pendingNotes = null!;
    private ITurnIntentExplainGuarantee _turnIntent = null!;
    private Guid _agentId;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _agentId = Guid.NewGuid();
        _userId = Guid.NewGuid();
        _pendingNotes = Substitute.For<IPendingUserNoteRepository>();
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);
        _turnIntent = new NoTurnIntentExplainGuarantee();
    }

    private SkillToolsetGuaranteeResolver CreateResolver()
    {
        var relations = Substitute.For<ISkillRelationRepository>();
        relations.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var skillRepository = SkillRepositoryStubs.WithoutRequirements();
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepository);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        var recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());

        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());

        return new SkillToolsetGuaranteeResolver(
            _pendingNotes,
            recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(),
            phrases,
            relations,
            _turnIntent,
            Substitute.For<IScenarioActivityProbe>(),
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());
    }

    private static AgentSkill Skill(string name, bool alwaysOn = false, string triggerKeywords = "[]") => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = SkillEffect.Mutate,
        AlwaysOn = alwaysOn,
        TriggerKeywords = triggerKeywords
    };

    private async Task<SkillToolsetGuaranteeResult> Resolve(
        IReadOnlyList<AgentSkill> permitted,
        string message = NeutralMessage,
        IReadOnlyList<AgentSkill>? retrieved = null,
        IReadOnlyCollection<string>? pinned = null,
        string? userId = null)
    {
        var request = new SkillToolsetGuaranteeRequest(
            _agentId, permitted, retrieved ?? [], message, "conv", null, userId ?? _userId.ToString(), "de", [],
            pinned, null, null, true);

        return await CreateResolver().ResolveAsync(request, CancellationToken.None);
    }

    private static List<string> Names(SkillToolsetGuaranteeResult result) =>
        result.GuaranteedSkills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Test]
    public async Task NeutralMessageWithoutState_GuaranteesNothing()
    {
        var result = await Resolve([Skill(KeywordSkill), Skill(PendingNotesSkill), Skill(CutShiftSkill)]);

        Names(result).ShouldBeEmpty();
        result.GuaranteedSources.ShouldBeEmpty();
        result.LowestRankedSkillNames.ShouldBeEmpty();
        result.TurnIntentVetoesForcedToolCall.ShouldBeFalse();
    }

    [Test]
    public async Task TurnIntentExplainSkills_AreHints_AndItsVetoIsPassedThrough()
    {
        var turnIntent = Substitute.For<ITurnIntentExplainGuarantee>();
        turnIntent.ResolveAsync(Arg.Any<SkillToolsetGuaranteeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TurnIntentGuaranteeOutcome([ExplainSkill, "explain_not_permitted"], true));
        _turnIntent = turnIntent;

        var result = await Resolve([Skill(ExplainSkill)]);

        Names(result).ShouldBe([ExplainSkill]);
        result.GuaranteedSources[ExplainSkill].ShouldBe(ToolsetSkillSource.Hint);
        result.TurnIntentVetoesForcedToolCall.ShouldBeTrue();
    }

    [Test]
    public async Task KeywordInTheMessage_GuaranteesTheSkillWithKeywordSource()
    {
        var result = await Resolve(
            [Skill(KeywordSkill, triggerKeywords: "[\"Feiertagskalender\"]")],
            "Stell den Feiertagskalender um.");

        Names(result).ShouldBe([KeywordSkill]);
        result.GuaranteedSources[KeywordSkill].ShouldBe(ToolsetSkillSource.Keyword);
    }

    [Test]
    public async Task KeywordOfAnAlwaysOnSkill_GuaranteesNothing()
    {
        var result = await Resolve(
            [Skill(KeywordSkill, alwaysOn: true, triggerKeywords: "[\"Feiertagskalender\"]")],
            "Stell den Feiertagskalender um.");

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task RetrievedCreateShift_GuaranteesCutShiftAsHint()
    {
        var createShift = Skill(CreateShiftSkill);

        var result = await Resolve([createShift, Skill(CutShiftSkill)], retrieved: [createShift]);

        Names(result).ShouldBe([CutShiftSkill]);
        result.GuaranteedSources[CutShiftSkill].ShouldBe(ToolsetSkillSource.Hint);
    }

    [Test]
    public async Task CutShift_IsNotGuaranteed_WithoutARetrievedCreateShift()
    {
        var result = await Resolve([Skill(CreateShiftSkill), Skill(CutShiftSkill)]);

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task MessageWithTwoMutationTargets_GuaranteesCreatePlanAsHint()
    {
        PlanTriggerHeuristic.IsPlanCandidate(TwoMutationsMessage, false).ShouldBeTrue();

        var result = await Resolve([Skill(PlanSkillDefaults.CreatePlanSkillName)], TwoMutationsMessage);

        result.GuaranteedSources[PlanSkillDefaults.CreatePlanSkillName].ShouldBe(ToolsetSkillSource.Hint);
    }

    [Test]
    public async Task MessageWithOneMutationTarget_DoesNotGuaranteeCreatePlan()
    {
        var result = await Resolve([Skill(PlanSkillDefaults.CreatePlanSkillName)], OneMutationMessage);

        Names(result).ShouldNotContain(PlanSkillDefaults.CreatePlanSkillName);
    }

    [Test]
    public async Task PendingNotes_AreGuaranteedAsHint_WhenTheUserHasUndeliveredNotes()
    {
        _pendingNotes.CountPendingAsync(_agentId, _userId, Arg.Any<CancellationToken>()).Returns(2);

        var result = await Resolve([Skill(PendingNotesSkill)]);

        Names(result).ShouldBe([PendingNotesSkill]);
        result.GuaranteedSources[PendingNotesSkill].ShouldBe(ToolsetSkillSource.Hint);
        await _pendingNotes.Received(1).CountPendingAsync(_agentId, _userId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PendingNotes_AreNotGuaranteed_WhenNothingIsPending()
    {
        var result = await Resolve([Skill(PendingNotesSkill)]);

        Names(result).ShouldBeEmpty();
    }

    [Test]
    public async Task PendingNotes_AreNeverCounted_WhenTheSkillIsNotPermitted()
    {
        await Resolve([Skill(KeywordSkill)]);

        await _pendingNotes.DidNotReceiveWithAnyArgs().CountPendingAsync(default, default, default);
    }

    [Test]
    public async Task PendingNotes_AreNeverCounted_WhenTheUserIdIsNoGuid()
    {
        _pendingNotes.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(2);

        var result = await Resolve([Skill(PendingNotesSkill)], userId: "not-a-guid");

        Names(result).ShouldBeEmpty();
        await _pendingNotes.DidNotReceiveWithAnyArgs().CountPendingAsync(default, default, default);
    }

    [Test]
    public async Task PinnedSkills_AreHints_AndUnpermittedPinsAreIgnored()
    {
        var result = await Resolve([Skill(PinnedSkill)], pinned: [PinnedSkill, "not_permitted_skill"]);

        Names(result).ShouldBe([PinnedSkill]);
        result.GuaranteedSources[PinnedSkill].ShouldBe(ToolsetSkillSource.Hint);
    }

    [Test]
    public async Task APinnedSkillThatIsAlsoAKeywordHit_KeepsTheStrongerKeywordSource()
    {
        var result = await Resolve(
            [Skill(KeywordSkill, triggerKeywords: "[\"Feiertagskalender\"]")],
            "Stell den Feiertagskalender um.",
            pinned: [KeywordSkill]);

        result.GuaranteedSkills.Count.ShouldBe(1);
        result.GuaranteedSources[KeywordSkill].ShouldBe(ToolsetSkillSource.Keyword);
    }
}
