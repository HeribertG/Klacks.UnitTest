// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies the order in which guaranteed skills survive the provider cap: after always-on skills and the
/// guaranteed flag, the strength of the guarantee decides (recipe over learned phrase over keyword over hint),
/// and the follow-through guarantee of the previous turn comes last. With a tiny budget the current message's
/// own keyword guarantee must therefore survive over a skill that was only kept for the follow-through. Within the
/// follow-through source the same-skill continuation ranks last, so the advised skills outlive it.
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
public class SkillToolsetAssemblerTruncationPriorityTests
{
    private const string AlwaysOnSkill = "navigate_to";
    private const string ExplainSkill = "explain_contract_setup";
    private const string FollowThroughSkill = "create_contract_from_template";
    private const string AdviseSkill = "evaluate_contract_templates";
    private const string KeywordSkill = "list_open_shifts";
    private const string ContinuationSkill = "update_contract";
    private const string KeywordPhrase = "offene dienste";
    private const string Message = "Zeig mir die offene dienste";
    private const string UserId = "user-1";

    private Agent _agent = null!;
    private SkillToolsetAssembler _assembler = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };

        var skillCache = Substitute.For<ISkillCacheService>();
        skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>
            {
                Skill(AlwaysOnSkill, SkillEffect.Mutate, alwaysOn: true),
                Skill(ExplainSkill, SkillEffect.Explain),
                Skill(FollowThroughSkill, SkillEffect.Mutate),
                Skill(AdviseSkill, SkillEffect.Advise),
                Skill(KeywordSkill, SkillEffect.Read, triggerKeywords: $"[\"{KeywordPhrase}\"]"),
                Skill(ContinuationSkill, SkillEffect.Mutate)
            });

        var retrieval = Substitute.For<IKnowledgeRetrievalService>();
        retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult([]));

        var queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<string>(0));

        var expander = Substitute.For<ISkillRetrievalExpander>();
        expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        var relations = Substitute.For<ISkillRelationRepository>();
        relations.GetByAgentAsync(_agent.Id, Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>
        {
            new()
            {
                AgentId = _agent.Id,
                SkillAName = FollowThroughSkill,
                SkillBName = ExplainSkill,
                Type = SkillRelationType.ExplainedBy,
                Status = SkillRelationStatus.Active
            },
            new()
            {
                AgentId = _agent.Id,
                SkillAName = AdviseSkill,
                SkillBName = FollowThroughSkill,
                Type = SkillRelationType.AdvisesFor,
                Status = SkillRelationStatus.Active
            }
        });

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

        _assembler = new SkillToolsetAssembler(
            skillCache, retrieval, queryBuilder, expander, resolver,
            Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

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

    private static AgentSkill Skill(
        string name, SkillEffect effect, bool alwaysOn = false, string triggerKeywords = "[]") => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = effect,
        AlwaysOn = alwaysOn,
        TriggerKeywords = triggerKeywords
    };

    private Task<SkillToolsetResult> Assemble(int maxTools, IReadOnlyList<string>? continuation = null) =>
        _assembler.AssembleAsync(
            _agent, new List<string>(), Message, conversationId: null, currentRoute: null, UserId, language: "de",
            maxTools, applyLearnedPhraseGuarantee: false, excludedSkillNames: null, pinnedSkillNames: null,
            previousTurnSkillNames: [ExplainSkill], continuationSkillNames: continuation, CancellationToken.None);

    [Test]
    public async Task WithRoomForBoth_TheKeywordAndTheFollowThroughSkillAreOffered()
    {
        var result = await Assemble(maxTools: 10);

        var names = result.Functions.Select(f => f.Name).ToList();
        names.ShouldContain(KeywordSkill);
        names.ShouldContain(FollowThroughSkill);
        result.Functions.Single(f => f.Name == FollowThroughSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.FollowThrough);
        result.Functions.Single(f => f.Name == KeywordSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.Keyword);
    }

    [Test]
    public async Task WithATinyBudget_TheKeywordGuaranteeSurvives_OverTheFollowThroughGuarantee()
    {
        var result = await Assemble(maxTools: 2);

        var names = result.Functions.Select(f => f.Name).ToList();
        names.ShouldContain(AlwaysOnSkill);
        names.ShouldContain(KeywordSkill);
        names.ShouldNotContain(FollowThroughSkill);
        names.ShouldNotContain(AdviseSkill);
        names.Count.ShouldBe(2);
    }

    [TestCase(ToolsetSkillSource.RecipeStep, ToolsetSkillSource.LearnedPhrase)]
    [TestCase(ToolsetSkillSource.LearnedPhrase, ToolsetSkillSource.Keyword)]
    [TestCase(ToolsetSkillSource.Keyword, ToolsetSkillSource.Hint)]
    [TestCase(ToolsetSkillSource.Hint, ToolsetSkillSource.FollowThrough)]
    public void SourcePriority_IsStrictlyOrdered(ToolsetSkillSource stronger, ToolsetSkillSource weaker)
    {
        stronger.Priority().ShouldBeGreaterThan(weaker.Priority());
    }

    [Test]
    public async Task WithRoomForAll_TheContinuationSkillIsOfferedAsFollowThrough()
    {
        var result = await Assemble(maxTools: 10, continuation: [ContinuationSkill]);

        result.Functions.Single(f => f.Name == ContinuationSkill).ToolsetSource.ShouldBe(ToolsetSkillSource.FollowThrough);
        result.Functions.Select(f => f.Name).ShouldContain(FollowThroughSkill);
        result.Functions.Select(f => f.Name).ShouldContain(AdviseSkill);
    }

    [Test]
    public async Task WithOneSlotTooFew_TheContinuationSkillIsDroppedBeforeTheAdvisedFollowThroughSkills()
    {
        var result = await Assemble(maxTools: 4, continuation: [ContinuationSkill]);

        var names = result.Functions.Select(f => f.Name).ToList();
        names.ShouldBe(
            new[] { AlwaysOnSkill, KeywordSkill, FollowThroughSkill, AdviseSkill }, ignoreOrder: true);
        names.ShouldNotContain(ContinuationSkill);
    }

    [Test]
    public async Task WithATinyBudget_TheKeywordGuaranteeStillSurvives_OverEveryFollowThroughSkill()
    {
        var result = await Assemble(maxTools: 2, continuation: [ContinuationSkill]);

        result.Functions.Select(f => f.Name).ShouldBe(new[] { AlwaysOnSkill, KeywordSkill }, ignoreOrder: true);
    }
}
