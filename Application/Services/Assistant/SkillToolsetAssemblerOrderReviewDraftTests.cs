// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The toolset while an order review is open in the conversation: its loop skills stay in the toolset on a keyword-free
/// answer turn ("07:00 passt"), and the skills that would change, seal, cut or regroup orders past the review's own
/// confirmed preview are left out even when retrieval ranks them first. Without a review nothing changes. Wires the
/// real SkillToolsetGuaranteeResolver, DraftLoopToolsetPolicy and order-review draft store into the assembler.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetAssemblerOrderReviewDraftTests
{
    private const string ConversationId = "conv-order-review";
    private const string AnswerTurn = "07:00 passt, und keine Spesen";

    private static readonly string[] LoopSkills =
        ["set_order_review_decision", "preview_order_review", "execute_order_review", "cancel_order_review"];

    private static readonly string[] BypassSkills =
        ["update_shift", "seal_shift", "cut_shift", "cut_shift_by_date", "assign_orders_to_groups", "add_shift_to_group"];

    private ISkillCacheService _skillCache = null!;
    private IKnowledgeRetrievalService _retrieval = null!;
    private IPendingOrderReviewDraftStore _orderReviewDraftStore = null!;
    private Agent _agent = null!;

    [SetUp]
    public void SetUp()
    {
        _agent = new Agent { Id = Guid.NewGuid() };
        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetEnabledSkillsAsync(_agent.Id, Arg.Any<CancellationToken>())
            .Returns(LoopSkills.Concat(BypassSkills).Select(CreateSkill).ToList());
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _retrieval.RetrieveAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<KnowledgeEntryKind?>())
            .Returns(new RetrievalResult(BypassSkills.Select(name => new RetrievalCandidate(
                new KnowledgeEntry { Id = Guid.NewGuid(), Kind = KnowledgeEntryKind.Skill, SourceId = name, Text = name }, 0.9)).ToList()));
        _orderReviewDraftStore = PendingStoreTestFactory.CreateOrderReviewDraftStore();
    }

    private static AgentSkill CreateSkill(string name) => new()
    {
        Effect = SkillEffect.Mutate,
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        TriggerKeywords = "[]",
    };

    private SkillToolsetAssembler CreateAssembler()
    {
        var recipeRepository = Substitute.For<IAgentRecipeRepository>();
        recipeRepository.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IAgentRecipeRepository)).Returns(recipeRepository);
        var skillRepository = SkillRepositoryStubs.WithoutRequirements();
        scopedProvider.GetService(typeof(IAgentSkillRepository)).Returns(skillRepository);
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
            PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(orderReviewDraftStore: _orderReviewDraftStore),
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
            _skillCache, _retrieval, queryBuilder, expander, resolver, Substitute.For<ILogger<SkillToolsetAssembler>>());
    }

    private async Task<List<string>> AssembleAsync(Guid userId)
    {
        var result = await CreateAssembler().AssembleAsync(
            _agent, new List<string>(), AnswerTurn, ConversationId, currentRoute: null, userId.ToString(), language: "de");
        return result.Functions.Select(f => f.Name).ToList();
    }

    [Test]
    public async Task OpenOrderReview_KeepsItsLoopSkills_AndLeavesTheBypassSkillsOut()
    {
        var userId = Guid.NewGuid();
        _orderReviewDraftStore.Set(userId, ConversationId, new OrderReviewDraft { Items = [new OrderReviewDraftItem()] });

        var names = await AssembleAsync(userId);

        foreach (var loopSkill in LoopSkills)
        {
            names.ShouldContain(loopSkill);
        }

        foreach (var bypassSkill in BypassSkills)
        {
            names.ShouldNotContain(bypassSkill);
        }
    }

    [Test]
    public async Task WithoutOrderReview_NothingIsGuaranteedOrLeftOut()
    {
        var names = await AssembleAsync(Guid.NewGuid());

        names.ShouldContain("seal_shift");
        names.ShouldNotContain("execute_order_review");
    }
}
