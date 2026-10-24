// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies that the provider cap cuts the retrieved skills by retrieval score, best first, instead of by SortOrder
/// (0 for practically every skill, which left the cut to the order of the skill cache). The score only ranks inside
/// the tier the guarantees leave open: always-on skills and guaranteed skills survive every retrieved skill however
/// well it scored, and the same-skill continuation a bare "yes" needs is such a guarantee. Exact ties are broken by
/// name, so the cut does not depend on the cache order.
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
public class SkillToolsetAssemblerScoreTruncationTests
{
    private const string AlwaysOnSkill = "navigate_to";
    private const string ContinuationSkill = "update_contract";
    private const string KeywordSkill = "list_open_shifts";
    private const string KeywordPhrase = "offene dienste";
    private const string UserId = "user-1";
    private const double BestScore = 0.9;
    private const double GoodScore = 0.7;
    private const double MiddleScore = 0.5;
    private const double WeakScore = 0.3;

    private Agent _agent = null!;

    private static AgentSkill Skill(
        string name, SkillEffect effect = SkillEffect.Read, bool alwaysOn = false, string triggerKeywords = "[]",
        int sortOrder = 0) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = effect,
        AlwaysOn = alwaysOn,
        TriggerKeywords = triggerKeywords,
        SortOrder = sortOrder
    };

    private static RetrievalCandidate Candidate(string skillName, double score) =>
        new(new KnowledgeEntry { SourceId = skillName, Kind = KnowledgeEntryKind.Skill }, score);

    private static RecipeEngineService CreateRecipeEngine()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepositoryStub = Klacks.UnitTest.TestHelpers.SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepositoryStub);
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        return new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());
    }

    private SkillToolsetAssembler CreateAssembler(
        IReadOnlyList<AgentSkill> cacheOrder, IReadOnlyList<RetrievalCandidate> candidates)
    {
        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(cacheOrder.ToList());

        var retrieval = Substitute.For<IKnowledgeRetrievalService>();
        retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(candidates));

        var queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));

        var expander = Substitute.For<ISkillRetrievalExpander>();
        expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        var relations = Substitute.For<ISkillRelationRepository>();
        relations.GetByAgentAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());

        var resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            CreateRecipeEngine(),
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(),
            phrases,
            relations,
            new NoTurnIntentExplainGuarantee(),
            Substitute.For<IScenarioActivityProbe>(),
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());

        return new SkillToolsetAssembler(
            skillCache, retrieval, queryBuilder, expander, resolver,
            Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    [SetUp]
    public void SetUp() => _agent = new Agent { Id = Guid.NewGuid() };

    private static Task<SkillToolsetResult> Assemble(
        SkillToolsetAssembler assembler, Agent agent, string message, int maxTools,
        IReadOnlyList<string>? continuation = null) =>
        assembler.AssembleAsync(
            agent, new List<string>(), message, conversationId: null, currentRoute: null, UserId, language: "de",
            maxTools, applyLearnedPhraseGuarantee: false, excludedSkillNames: null, pinnedSkillNames: null,
            previousTurnSkillNames: null, continuationSkillNames: continuation, CancellationToken.None);

    private static List<string> Names(SkillToolsetResult result) => result.Functions.Select(f => f.Name).ToList();

    [Test]
    public async Task WhenTheCapCutsRetrievedSkills_TheBestScoredSurvive_WhateverTheCacheOrder()
    {
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill("alpha_skill"), Skill("bravo_skill"), Skill("charlie_skill"), Skill("delta_skill")
        };
        var candidates = new[]
        {
            Candidate("alpha_skill", WeakScore), Candidate("bravo_skill", MiddleScore),
            Candidate("charlie_skill", GoodScore), Candidate("delta_skill", BestScore)
        };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "something unrelated", maxTools: 3);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, "charlie_skill", "delta_skill" }, ignoreOrder: true);
    }

    [Test]
    public async Task TheScoreOutranksSortOrder()
    {
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill("early_sorted_weak", sortOrder: 0), Skill("late_sorted_strong", sortOrder: 99)
        };
        var candidates = new[] { Candidate("early_sorted_weak", WeakScore), Candidate("late_sorted_strong", BestScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "something unrelated", maxTools: 2);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, "late_sorted_strong" }, ignoreOrder: true);
    }

    [Test]
    public async Task AGuaranteedSkillSurvivesEveryRetrievedSkill_HoweverWellItScored()
    {
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill(KeywordSkill, triggerKeywords: $"[\"{KeywordPhrase}\"]"),
            Skill("strong_retrieved"), Skill("other_retrieved")
        };
        var candidates = new[] { Candidate("strong_retrieved", BestScore), Candidate("other_retrieved", GoodScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "Zeig mir die offene dienste", maxTools: 2);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, KeywordSkill }, ignoreOrder: true);
    }

    [Test]
    public async Task ABareYes_KeepsTheSameSkillContinuation_OverEveryRetrievedSkill()
    {
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill(ContinuationSkill, SkillEffect.Mutate),
            Skill("strong_retrieved"), Skill("other_retrieved")
        };
        var candidates = new[] { Candidate("strong_retrieved", BestScore), Candidate("other_retrieved", GoodScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "ja", maxTools: 3, continuation: [ContinuationSkill]);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, ContinuationSkill, "strong_retrieved" }, ignoreOrder: true);
        result.Functions.Single(f => f.Name == ContinuationSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.FollowThrough);
    }

    [Test]
    public async Task EqualScores_AreCutByName_NotByCacheOrder()
    {
        var firstOrder = new[] { Skill(AlwaysOnSkill, alwaysOn: true), Skill("zulu_skill"), Skill("alpha_skill"), Skill("mike_skill") };
        var reversedOrder = firstOrder.Reverse().ToArray();
        var candidates = new[]
        {
            Candidate("zulu_skill", MiddleScore), Candidate("alpha_skill", MiddleScore), Candidate("mike_skill", MiddleScore)
        };

        var first = await Assemble(CreateAssembler(firstOrder, candidates), _agent, "something unrelated", maxTools: 3);
        var second = await Assemble(CreateAssembler(reversedOrder, candidates), _agent, "something unrelated", maxTools: 3);

        Names(first).ShouldBe(new[] { AlwaysOnSkill, "alpha_skill", "mike_skill" }, ignoreOrder: true);
        Names(second).ShouldBe(Names(first), ignoreOrder: true);
    }

    [Test]
    public async Task WithinOneGuaranteeTier_ASkillWithAScoreOutranksOneWithout_WhateverTheirSortOrder()
    {
        var keywordJson = $"[\"{KeywordPhrase}\"]";
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill("unscored_guarantee", triggerKeywords: keywordJson, sortOrder: 0),
            Skill("scored_guarantee", triggerKeywords: keywordJson, sortOrder: 9)
        };
        var candidates = new[] { Candidate("scored_guarantee", WeakScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "Zeig mir die offene dienste", maxTools: 2);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, "scored_guarantee" }, ignoreOrder: true);
    }

    [Test]
    public async Task WithinOneGuaranteeTier_TwoScoredSkills_AreCutByScore()
    {
        var keywordJson = $"[\"{KeywordPhrase}\"]";
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill("weak_guarantee", triggerKeywords: keywordJson),
            Skill("strong_guarantee", triggerKeywords: keywordJson)
        };
        var candidates = new[] { Candidate("weak_guarantee", WeakScore), Candidate("strong_guarantee", BestScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "Zeig mir die offene dienste", maxTools: 2);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, "strong_guarantee" }, ignoreOrder: true);
    }

    [Test]
    public async Task EqualScores_AreCutBySortOrderBeforeTheName()
    {
        var skills = new[]
        {
            Skill(AlwaysOnSkill, alwaysOn: true),
            Skill("alpha_skill", sortOrder: 5), Skill("zulu_skill", sortOrder: 1)
        };
        var candidates = new[] { Candidate("alpha_skill", MiddleScore), Candidate("zulu_skill", MiddleScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "something unrelated", maxTools: 2);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, "zulu_skill" }, ignoreOrder: true);
    }

    [Test]
    public async Task WithRoomForEverything_NothingIsCut()
    {
        var skills = new[] { Skill(AlwaysOnSkill, alwaysOn: true), Skill("alpha_skill"), Skill("bravo_skill") };
        var candidates = new[] { Candidate("alpha_skill", WeakScore), Candidate("bravo_skill", BestScore) };
        var assembler = CreateAssembler(skills, candidates);

        var result = await Assemble(assembler, _agent, "something unrelated", maxTools: 10);

        Names(result).ShouldBe(new[] { AlwaysOnSkill, "alpha_skill", "bravo_skill" }, ignoreOrder: true);
    }
}
