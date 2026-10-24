// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A keyword match on broad terms only ("schicht" in more skills than the guarantee cap holds) is still offered,
/// but as BroadKeyword: at the provider cap it competes with the retrieved skills on its retrieval score instead of
/// evicting the best-ranked one (owner decision 2026-10-24). A specific keyword keeps its full guarantee.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerBroadKeywordTests
{
    private const string AlwaysOnSkill = "navigate_to";
    private const string RetrievedSkill = "list_shifts_of_day";
    private const string SpecificSkill = "create_night_shift";
    private const string BroadTerm = "schicht";
    private const string SpecificTerm = "nachtschicht";
    private const string UserId = "user-1";
    private const double RetrievedScore = 0.9;

    private static readonly string[] BroadSkills =
    [
        "broad_rule_a", "broad_rule_b", "broad_rule_c", "broad_rule_d", "broad_rule_e", "broad_rule_f"
    ];

    private Agent _agent = null!;
    private SkillToolsetAssembler _assembler = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };

        var skills = new List<AgentSkill>
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill(RetrievedSkill),
            Skill(SpecificSkill, keyword: SpecificTerm)
        };
        skills.AddRange(BroadSkills.Select(name => Skill(name, keyword: BroadTerm)));

        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(skills);

        var retrieval = Substitute.For<IKnowledgeRetrievalService>();
        retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(
                    new KnowledgeEntry
                    {
                        Id = Guid.NewGuid(),
                        Kind = KnowledgeEntryKind.Skill,
                        SourceId = RetrievedSkill,
                        Text = $"{RetrievedSkill}. A skill."
                    },
                    RetrievedScore)
            ]));

        var queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));

        var expander = Substitute.For<ISkillRetrievalExpander>();
        expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());

        var resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            CreateRecipeEngine(),
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreatePlanningProfileDraftStore(),
            phrases,
            Substitute.For<ISkillRelationRepository>(),
            new NoTurnIntentExplainGuarantee(),
            Substitute.For<IScenarioActivityProbe>(),
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());

        _assembler = new SkillToolsetAssembler(
            skillCache, retrieval, queryBuilder, expander, resolver,
            Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    [Test]
    public async Task WithRoom_BroadKeywordSkillsAreOffered_AsBroadKeyword()
    {
        var result = await Assemble("Zeig mir die Schicht von morgen", maxTools: 30);

        var broad = result.Functions.Where(f => BroadSkills.Contains(f.Name)).ToList();
        broad.ShouldNotBeEmpty();
        broad.ShouldAllBe(f => f.ToolsetSource == ToolsetSkillSource.BroadKeyword);
    }

    [Test]
    public async Task WithATightBudget_TheRetrievedSkillSurvives_OverBroadKeywordSkills()
    {
        var result = await Assemble("Zeig mir die Schicht von morgen", maxTools: 2);

        result.Functions.Select(f => f.Name).ShouldBe(new[] { AlwaysOnSkill, RetrievedSkill }, ignoreOrder: true);
    }

    [Test]
    public async Task WithATightBudget_ASpecificKeywordStillSurvives_OverTheRetrievedSkill()
    {
        var result = await Assemble("Erstelle eine Nachtschicht für morgen", maxTools: 2);

        result.Functions.Select(f => f.Name).ShouldBe(new[] { AlwaysOnSkill, SpecificSkill }, ignoreOrder: true);
        result.Functions.Single(f => f.Name == SpecificSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.Keyword);
    }

    private Task<SkillToolsetResult> Assemble(string message, int maxTools) =>
        _assembler.AssembleAsync(
            _agent, new List<string>(), message, conversationId: null, currentRoute: null, UserId, language: "de",
            maxTools, applyLearnedPhraseGuarantee: false, excludedSkillNames: null, pinnedSkillNames: null,
            previousTurnSkillNames: null, continuationSkillNames: null, CancellationToken.None);

    private static RecipeEngineService CreateRecipeEngine()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(SkillRepositoryStubs.WithoutRequirements());
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        return new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private static AgentSkill Skill(string name, bool alwaysOn = false, string? keyword = null) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = SkillEffect.Read,
        AlwaysOn = alwaysOn,
        TriggerKeywords = keyword == null ? "[]" : $"[\"{keyword}\"]"
    };
}
