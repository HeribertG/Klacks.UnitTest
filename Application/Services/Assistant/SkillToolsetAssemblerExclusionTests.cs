// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The correction turn's toolset: the previous turn's skills are dropped, always-on skills and
/// confirm_pending_action survive the exclusion (including when confirm_pending_action is itself
/// configured non-always-on, isolating the explicit Remove of its name from the excluded set), a
/// RecipeStep-guaranteed skill survives the exclusion too (the recipe engine's step decision is more
/// specific than the turn-level exclusion), a pinned candidate is guaranteed back in but only when the
/// caller actually has the rights for it, an excluded skill stays out even when the co-required
/// expansion tries to re-add it, a guaranteed skill carries its retrieval score, and the legacy overload
/// behaves exactly as before. The last test is the positional-binding guard from 2026-09-14: called with
/// every legacy parameter positional (no named cancellationToken), it must still resolve to the short
/// overload and exclude nothing.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerExclusionTests
{
    private const string WrongSkill = "find_customer_candidates";
    private const string RightSkill = "search_employees";
    private const string PinnedSkill = "fill_group_by_criteria";
    private const string AlwaysOnSkill = "get_current_user";
    private const string RestrictedSkill = "delete_group";
    private const string UserMessage = "Trag alle Mitarbeitenden in die Gruppe ein.";
    private const string RecipeTriggerMessage = "Erstelle einen Dienst und teile ihn auf.";

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IRetrievalQueryBuilder _retrievalQueryBuilder = null!;
    private ISkillRetrievalExpander _expander = null!;
    private IPendingUserNoteRepository _pendingUserNoteRepository = null!;
    private RecipeEngineService _recipeEngine = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };

        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                Skill(WrongSkill),
                Skill(RightSkill),
                Skill(PinnedSkill),
                Skill(AutonomyDefaults.ConfirmPendingActionSkillName, alwaysOn: true),
                Skill(AlwaysOnSkill, alwaysOn: true),
                Skill(RestrictedSkill, requiredPermission: Permissions.CanDeleteGroups)
            });

        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult([Candidate(WrongSkill, 0.9), Candidate(RightSkill, 0.8)]));

        _retrievalQueryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        _retrievalQueryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));

        _expander = Substitute.For<ISkillRetrievalExpander>();
        _expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        _pendingUserNoteRepository = Substitute.For<IPendingUserNoteRepository>();
        _pendingUserNoteRepository.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var competingDetector = Substitute.For<ICompetingSkillIntentDetector>();
        competingDetector.FindCompetingSkillNamesAsync(default!, default, default!, default, default!, default)
            .ReturnsForAnyArgs(Array.Empty<string>());
        scopedProvider.GetService(typeof(ICompetingSkillIntentDetector)).Returns(competingDetector);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        _recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static AgentSkill Skill(string name, bool alwaysOn = false, string? requiredPermission = null) => new()
    {
        Name = name,
        Description = $"{name} description.",
        ParametersJson = "[]",
        AlwaysOn = alwaysOn,
        RequiredPermission = requiredPermission
    };

    private static RetrievalCandidate Candidate(string skillName, double score) => new(
        new KnowledgeEntry
        {
            Id = Guid.NewGuid(),
            Kind = KnowledgeEntryKind.Skill,
            SourceId = skillName,
            Text = $"{skillName}."
        },
        score);

    private SkillToolsetAssembler CreateAssembler() => new(
        _skillCache, _retrieval, _retrievalQueryBuilder, _expander,
        new SkillToolsetGuaranteeResolver(
            _pendingUserNoteRepository, _recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(),
            NoLearnedPhrases(),
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>()),
        Substitute.For<ILogger<SkillToolsetAssembler>>());

    private static ISkillPhraseRepository NoLearnedPhrases()
    {
        var repository = Substitute.For<ISkillPhraseRepository>();
        repository.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());
        return repository;
    }

    private Task<SkillToolsetResult> Assemble(
        IReadOnlyCollection<string>? excluded, IReadOnlyCollection<string>? pinned) =>
        Assemble(UserMessage, new List<string>(), excluded, pinned);

    private Task<SkillToolsetResult> Assemble(
        string userMessage,
        List<string> userRights,
        IReadOnlyCollection<string>? excluded,
        IReadOnlyCollection<string>? pinned) =>
        CreateAssembler().AssembleAsync(
            _agent, userRights, userMessage, null, null, Guid.NewGuid().ToString(), "de",
            KnowledgeIndexConstants.MaxToolsForProvider, true, excluded, pinned, CancellationToken.None);

    [Test]
    public async Task ExcludedSkill_IsNotInTheToolset()
    {
        var result = await Assemble([WrongSkill], null);

        result.Functions.ShouldNotContain(f => f.Name == WrongSkill);
        result.Functions.ShouldContain(f => f.Name == RightSkill);
    }

    [Test]
    public async Task AlwaysOnSkill_SurvivesTheExclusion()
    {
        var result = await Assemble([AlwaysOnSkill], null);

        result.Functions.ShouldContain(f => f.Name == AlwaysOnSkill);
    }

    [Test]
    public async Task ConfirmPendingAction_SurvivesTheExclusion_ThroughTheAlwaysOnExemption()
    {
        var result = await Assemble([AutonomyDefaults.ConfirmPendingActionSkillName], null);

        result.Functions.ShouldContain(f => f.Name == AutonomyDefaults.ConfirmPendingActionSkillName);
    }

    [Test]
    public async Task ConfirmPendingAction_SurvivesExclusion_EvenWhenConfiguredNotAlwaysOn()
    {
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                Skill(AutonomyDefaults.ConfirmPendingActionSkillName, alwaysOn: false)
            });

        var result = await Assemble(
            [AutonomyDefaults.ConfirmPendingActionSkillName],
            [AutonomyDefaults.ConfirmPendingActionSkillName]);

        result.Functions.ShouldContain(f => f.Name == AutonomyDefaults.ConfirmPendingActionSkillName);
    }

    [Test]
    public async Task RecipeStepGuaranteedSkill_SurvivesTheExclusion()
    {
        var result = await Assemble(RecipeTriggerMessage, new List<string>(), [WrongSkill], null);

        result.Functions.ShouldContain(f => f.Name == WrongSkill);
    }

    [Test]
    public async Task ExcludedSkill_IsDroppedEvenWhenTheExpansionReAddsIt()
    {
        _expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill> { Skill(WrongSkill) });

        var result = await Assemble([WrongSkill], null);

        result.Functions.ShouldNotContain(f => f.Name == WrongSkill);
    }

    [Test]
    public async Task PinnedSkill_UserLacksRightsFor_IsNotAdded()
    {
        var result = await Assemble(null, [RestrictedSkill]);

        result.Functions.ShouldNotContain(f => f.Name == RestrictedSkill);
    }

    [Test]
    public async Task PinnedSkill_IsGuaranteedEvenWhenRetrievalMissedIt()
    {
        var result = await Assemble(null, [PinnedSkill]);

        result.Functions.ShouldContain(f => f.Name == PinnedSkill);
    }

    [Test]
    public async Task ExclusionWins_WhenASkillIsPinnedAndExcludedAtOnce()
    {
        var result = await Assemble([PinnedSkill], [PinnedSkill]);

        result.Functions.ShouldNotContain(f => f.Name == PinnedSkill);
    }

    [Test]
    public async Task GuaranteedSkill_CarriesItsRetrievalScore()
    {
        var result = await Assemble(null, [RightSkill]);

        result.Functions.First(f => f.Name == RightSkill).RetrievalScore.ShouldBe(0.8);
    }

    [Test]
    public async Task PinnedSkillWithoutARetrievalScore_CarriesNone()
    {
        var result = await Assemble(null, [PinnedSkill]);

        result.Functions.First(f => f.Name == PinnedSkill).RetrievalScore.ShouldBeNull();
    }

    [Test]
    public async Task LegacyOverload_ExcludesNothing()
    {
        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), UserMessage, null, null, Guid.NewGuid().ToString(), "de",
            KnowledgeIndexConstants.MaxToolsForProvider, true, CancellationToken.None);

        result.Functions.ShouldContain(f => f.Name == WrongSkill);
        result.Functions.ShouldContain(f => f.Name == RightSkill);
    }
}
