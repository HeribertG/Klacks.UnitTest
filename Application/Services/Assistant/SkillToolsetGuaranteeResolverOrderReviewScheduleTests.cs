// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// OrderReviewScheduleGuarantee wired through SkillToolsetGuaranteeResolver: after a turn that ran execute_order_review,
/// an affirmative reply keeps open_schedule as a Hint next to the same-skill continuation of execute_order_review, also
/// while the review is still open (more orders left) and open_schedule is not among the review's exclusions. The
/// guarantee cannot tell which question the yes answers: a yes to the next review question keeps open_schedule as a hint
/// as well (accepted behaviour, visibility only), beside the review's loop skills.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.OrderReview;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillToolsetGuaranteeResolverOrderReviewScheduleTests
{
    private const string ConversationId = "conv-order-review-schedule";
    private const string OpenSchedule = "open_schedule";
    private const string ExecuteOrderReview = "execute_order_review";
    private const string SetOrderReviewDecision = "set_order_review_decision";
    private const string ScheduleYes = "Ja, zeig sie mir im Einsatzplan.";
    private const string NextQuestionYes = "Ja, Beginn 07:00 passt.";

    private IPendingOrderReviewDraftStore _orderReviewDraftStore = null!;
    private SkillToolsetGuaranteeResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        var relations = Substitute.For<ISkillRelationRepository>();
        relations.GetByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<SkillRelation>());

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

        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());
        var scenarioProbe = Substitute.For<IScenarioActivityProbe>();
        scenarioProbe.HasVisibleActivityAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        _orderReviewDraftStore = PendingStoreTestFactory.CreateOrderReviewDraftStore();
        _resolver = new SkillToolsetGuaranteeResolver(
            Substitute.For<IPendingUserNoteRepository>(),
            recipeEngine,
            PendingStoreTestFactory.CreateConfirmationStore(),
            PendingStoreTestFactory.CreateDraftLoopToolsetPolicy(orderReviewDraftStore: _orderReviewDraftStore),
            phrases,
            relations,
            new NoTurnIntentExplainGuarantee(),
            scenarioProbe,
            Substitute.For<ILogger<SkillToolsetGuaranteeResolver>>());
    }

    [TearDown]
    public void ResetPluginEntries()
    {
        AffirmationDetector.Reset();
        DeclineDetector.Reset();
    }

    [Test]
    public async Task YesAfterExecuteOrderReview_KeepsOpenScheduleAsHint_BesideTheContinuation()
    {
        var result = await Resolve(Guid.NewGuid(), ScheduleYes, [ExecuteOrderReview]);

        Names(result).ShouldContain(OpenSchedule);
        result.GuaranteedSources[OpenSchedule].ShouldBe(ToolsetSkillSource.Hint);
        result.GuaranteedSources[ExecuteOrderReview].ShouldBe(ToolsetSkillSource.FollowThrough);
        result.DraftLoopOpen.ShouldBeFalse();
    }

    [Test]
    public async Task YesAfterAPartialExecute_WithTheReviewStillOpen_KeepsOpenScheduleAndIsNotExcluded()
    {
        var userId = OpenOrderReview();

        var result = await Resolve(userId, ScheduleYes, [ExecuteOrderReview]);

        result.DraftLoopOpen.ShouldBeTrue();
        Names(result).ShouldContain(OpenSchedule);
        result.GuaranteedSources[OpenSchedule].ShouldBe(ToolsetSkillSource.Hint);
        result.ExcludedSkillNames.ShouldNotBeNull();
        result.ExcludedSkillNames!.ShouldNotContain(OpenSchedule);
    }

    [Test]
    public async Task YesToTheNextReviewQuestion_AfterExecute_AlsoKeepsOpenScheduleButOnlyAsHint()
    {
        var userId = OpenOrderReview();

        var result = await Resolve(userId, NextQuestionYes, [ExecuteOrderReview]);

        Names(result).ShouldContain(SetOrderReviewDecision);
        Names(result).ShouldContain(OpenSchedule);
        result.GuaranteedSources[OpenSchedule].ShouldBe(ToolsetSkillSource.Hint);
    }

    [Test]
    public async Task YesWithoutExecuteOrderReviewBefore_DoesNotKeepOpenSchedule()
    {
        var result = await Resolve(Guid.NewGuid(), ScheduleYes, continuation: null);

        Names(result).ShouldNotContain(OpenSchedule);
    }

    private Guid OpenOrderReview()
    {
        var userId = Guid.NewGuid();
        _orderReviewDraftStore.Set(userId, ConversationId, new OrderReviewDraft { Items = [new OrderReviewDraftItem()] });
        return userId;
    }

    private Task<SkillToolsetGuaranteeResult> Resolve(Guid userId, string message, IReadOnlyList<string>? continuation)
    {
        var request = new SkillToolsetGuaranteeRequest(
            Guid.NewGuid(), Skills(), [], message, ConversationId, null, userId.ToString(), "de", [],
            null, null, continuation, true);

        return _resolver.ResolveAsync(request, CancellationToken.None);
    }

    private static List<AgentSkill> Skills() =>
    [
        Skill(OpenSchedule, SkillEffect.Read),
        Skill(ExecuteOrderReview, SkillEffect.Mutate),
        Skill(SetOrderReviewDecision, SkillEffect.Mutate),
    ];

    private static AgentSkill Skill(string name, SkillEffect effect) => new()
    {
        Name = name,
        Description = "A skill.",
        ParametersJson = "[]",
        Effect = effect,
        TriggerKeywords = "[]",
    };

    private static List<string> Names(SkillToolsetGuaranteeResult result) =>
        result.GuaranteedSkills.Select(s => s.Name).ToList();
}
