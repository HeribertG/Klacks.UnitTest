// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The recipe guarantee of the toolset while a draft loop (order review or planning-profile setup) is open: it agrees with TurnPreparationService and matches by
/// keyword trigger only, so an answer to the review ("Passt, übernimm alle Vorschläge, keine Spesen.") guarantees no step
/// skill of a recipe the semantic fallback would rank it into, while an explicit trigger phrase still guarantees its
/// recipe's skills. A planning-profile draft alone ("Alle Mitarbeiter mit 8.5 Stunden, Sicherheit zuerst.") switches the match the same
/// way. The toolset carries DraftLoopOpen for the chat entry points. Wires the real SkillToolsetGuaranteeResolver,
/// DraftLoopToolsetPolicy, order-review and planning-profile draft stores and RecipeEngineService into the assembler.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Skills.PlanningProfile;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerDraftLoopRecipeTests
{
    private const string ConversationId = "conv-order-review-recipe";
    private const string LoopAnswer = "Passt, übernimm alle Vorschläge, keine Spesen.";
    private const string KeywordRequest = "Erstelle einen neuen Mitarbeiter";
    private const string GroupRecipeName = "bulk-add-employees-to-group";
    private const string GroupRecipeSkill = "bulk_add_clients_to_group";
    private const string EmployeeRecipeSkill = "create_employee";
    private const string LoopSkill = "set_order_review_decision";
    private const string PlanningProfileLoopSkill = "set_planning_profile_parameters";
    private const string PlanningProfileAnswer = "Alle Mitarbeiter mit 8.5 Stunden, Sicherheit zuerst.";
    private const double StrongScore = 0.85;

    private static readonly AgentRecipe GroupRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = GroupRecipeName,
        Goal = "Add several employees to a group.",
        TriggerJson = """{"allOf":[{"anyWordStart":["hinzufüg"]},{"anyWordStart":["alle"]},{"anySubstring":["gruppe"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"bulk_add_clients_to_group"}]""",
        IsEnabled = true,
        SortOrder = 1
    };

    private static readonly AgentRecipe EmployeeRecipe = new()
    {
        Id = Guid.NewGuid(),
        Name = "create-employee",
        Goal = "Create an employee.",
        TriggerJson = """{"allOf":[{"anyWordStart":["erstell"]},{"anySubstring":["mitarbeiter"]}],"noneOf":[]}""",
        StepsJson = """[{"kind":"mutate","skill":"create_employee"}]""",
        IsEnabled = true,
        SortOrder = 2
    };

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _skillRetrieval = null!;
    private IKnowledgeRetrievalService _recipeRetrieval = null!;
    private IPendingOrderReviewDraftStore _orderReviewDraftStore = null!;
    private IPendingPlanningProfileDraftStore _planningProfileDraftStore = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(new[] { LoopSkill, PlanningProfileLoopSkill, GroupRecipeSkill, EmployeeRecipeSkill }
                .Select(CreateSkill).ToList());

        _skillRetrieval = Substitute.For<IKnowledgeRetrievalService>();
        _skillRetrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult([]));

        _recipeRetrieval = Substitute.For<IKnowledgeRetrievalService>();
        _recipeRetrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>(),
                Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(new RetrievalResult(
            [
                new RetrievalCandidate(
                    new KnowledgeEntry { Kind = KnowledgeEntryKind.Recipe, SourceId = GroupRecipeName, Text = "irrelevant" },
                    StrongScore)
            ]));

        _orderReviewDraftStore = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        _planningProfileDraftStore = PendingStoreTestFactory.CreatePlanningProfileDraftStore();
    }

    [TearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
        DeclineDetector.Reset();
    }

    [Test]
    public async Task OpenOrderReview_TheReviewAnswer_GuaranteesNoRecipeBySimilarity()
    {
        var userId = OpenOrderReview();

        var result = await AssembleAsync(userId, LoopAnswer);

        result.DraftLoopOpen.ShouldBeTrue();
        Names(result).ShouldContain(LoopSkill);
        Names(result).ShouldNotContain(GroupRecipeSkill);
        await _recipeRetrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default, default);
    }

    [Test]
    public async Task OpenOrderReview_AnExplicitTriggerPhrase_StillGuaranteesItsRecipe()
    {
        var userId = OpenOrderReview();

        var result = await AssembleAsync(userId, KeywordRequest);

        Names(result).ShouldContain(EmployeeRecipeSkill);
    }

    [Test]
    public async Task WithoutOrderReview_TheSameAnswer_StillGuaranteesTheSemanticRecipe()
    {
        var result = await AssembleAsync(Guid.NewGuid(), LoopAnswer);

        result.DraftLoopOpen.ShouldBeFalse();
        Names(result).ShouldContain(GroupRecipeSkill);
    }

    [Test]
    public async Task OpenPlanningProfileOnly_TheSetupAnswer_GuaranteesNoRecipeBySimilarity()
    {
        var userId = OpenPlanningProfileSetup();

        var result = await AssembleAsync(userId, PlanningProfileAnswer);

        result.DraftLoopOpen.ShouldBeTrue();
        Names(result).ShouldContain(PlanningProfileLoopSkill);
        Names(result).ShouldNotContain(LoopSkill);
        Names(result).ShouldNotContain(GroupRecipeSkill);
        await _recipeRetrieval.DidNotReceiveWithAnyArgs().RetrieveAsync(
            default!, default!, default, default, default, default, default, default);
    }

    [Test]
    public async Task OpenPlanningProfileOnly_AnExplicitTriggerPhrase_StillGuaranteesItsRecipe()
    {
        var userId = OpenPlanningProfileSetup();

        var result = await AssembleAsync(userId, KeywordRequest);

        result.DraftLoopOpen.ShouldBeTrue();
        Names(result).ShouldContain(EmployeeRecipeSkill);
    }

    [Test]
    public async Task WithoutPlanningProfile_TheSetupAnswer_StillGuaranteesTheSemanticRecipe()
    {
        var result = await AssembleAsync(Guid.NewGuid(), PlanningProfileAnswer);

        result.DraftLoopOpen.ShouldBeFalse();
        Names(result).ShouldContain(GroupRecipeSkill);
    }

    private Guid OpenPlanningProfileSetup()
    {
        var userId = Guid.NewGuid();
        _planningProfileDraftStore.Set(
            userId, PlanningProfileDraftScope.ConversationKey(ConversationId), new PlanningProfileDraft());
        return userId;
    }

    private Guid OpenOrderReview()
    {
        var userId = Guid.NewGuid();
        _orderReviewDraftStore.Set(userId, ConversationId, new OrderReviewDraft { Items = [new OrderReviewDraftItem()] });
        return userId;
    }

    private static List<string> Names(SkillToolsetResult result) => result.Functions.Select(f => f.Name).ToList();

    private static AgentSkill CreateSkill(string name) => new()
    {
        Effect = SkillEffect.Mutate,
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        TriggerKeywords = "[]",
    };

    private Task<SkillToolsetResult> AssembleAsync(Guid userId, string message) =>
        CreateAssembler().AssembleAsync(
            _agent, new List<string>(), message, ConversationId, currentRoute: null, userId.ToString(), language: "de");

    private SkillToolsetAssembler CreateAssembler()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AgentRecipe> { GroupRecipe, EmployeeRecipe });
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepository = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepository);
        scopedProvider.GetService(typeof(IRecipeCandidateRetriever))
            .Returns(new KnowledgeIndexRecipeCandidateRetriever(_recipeRetrieval));
        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);
        var recipeEngine = new RecipeEngineService(
            scopeFactory, Substitute.For<IPendingRecipeStore>(), Substitute.For<ILogger<RecipeEngineService>>());

        var skillPhrases = Substitute.For<ISkillPhraseRepository>();
        skillPhrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());
        var scenarioProbe = Substitute.For<IScenarioActivityProbe>();
        scenarioProbe.HasVisibleActivityAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var notes = Substitute.For<IPendingUserNoteRepository>();
        notes.CountPendingAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(0);

        var resolver = new SkillToolsetGuaranteeResolver(
            notes, recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(_planningProfileDraftStore, _orderReviewDraftStore),
            skillPhrases,
            Substitute.For<ISkillRelationRepository>(),
            new NoTurnIntentExplainGuarantee(),
            scenarioProbe,
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());

        var queryBuilder = Substitute.For<IRetrievalQueryBuilder>();
        queryBuilder.BuildAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string>(0));
        var expander = Substitute.For<ISkillRetrievalExpander>();
        expander.ExpandAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<AgentSkill>>(), Arg.Any<IReadOnlyList<AgentSkill>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkill>());

        return new SkillToolsetAssembler(
            _skillCache, _skillRetrieval, queryBuilder, expander, resolver, Substitute.For<ILogger<SkillToolsetAssembler>>());
    }
}
