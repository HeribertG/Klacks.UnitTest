// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DismissStreakEvaluator — verifies that exactly DismissStreakThreshold
/// consecutive dismissals of a kind fire a targeted MuteSuggestionTriggerEvent with the
/// per-kind dedup key, that an interposed helpful reaction or a shorter streak fires nothing,
/// that the mute_suggestion kind itself is excluded from streak tracking, and (at the trigger
/// service level) that the persisted dedup prevents a second mute question after a further
/// dismissal.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class DismissStreakEvaluatorTests
{
    private static readonly Guid UserGuid = Guid.NewGuid();
    private static readonly string UserId = UserGuid.ToString();

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentTriggerService _triggerService = null!;
    private DismissStreakEvaluator _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _sut = new DismissStreakEvaluator(_dispatchRepository, _triggerService);
    }

    private static ProactiveTriggerDispatchRow ReactionRow(ProactiveReaction reaction, int minutesAgo) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        TriggerKind = AgentTriggerKinds.UnstaffedShift,
        DedupKey = Guid.NewGuid().ToString(),
        Reaction = reaction,
        ReactionAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo)
    };

    private void SetRecentReactions(params ProactiveTriggerDispatchRow[] rows) =>
        _dispatchRepository
            .GetRecentReactionsAsync(UserId, AgentTriggerKinds.UnstaffedShift, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    [Test]
    public async Task EvaluateAsync_ThreeConsecutiveDismissals_FiresTargetedMuteSuggestion()
    {
        SetRecentReactions(
            ReactionRow(ProactiveReaction.Dismissed, 1),
            ReactionRow(ProactiveReaction.Dismissed, 2),
            ReactionRow(ProactiveReaction.Dismissed, 3));

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e =>
                e.Kind == AgentTriggerKinds.MuteSuggestion &&
                e.TargetUserId == UserGuid &&
                e.DedupKey == "mute-suggestion:" + AgentTriggerKinds.UnstaffedShift &&
                e.SummaryParams != null &&
                e.SummaryParams["kind"] == AgentTriggerKinds.UnstaffedShift),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateAsync_HelpfulReactionInterruptsStreak_FiresNothing()
    {
        SetRecentReactions(
            ReactionRow(ProactiveReaction.Dismissed, 1),
            ReactionRow(ProactiveReaction.Helpful, 2),
            ReactionRow(ProactiveReaction.Dismissed, 3));

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        await _triggerService.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task EvaluateAsync_OnlyTwoDismissals_FiresNothing()
    {
        SetRecentReactions(
            ReactionRow(ProactiveReaction.Dismissed, 1),
            ReactionRow(ProactiveReaction.Dismissed, 2));

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        await _triggerService.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task EvaluateAsync_MuteSuggestionKind_IsExcludedFromStreakTracking()
    {
        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.MuteSuggestion);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().GetRecentReactionsAsync(default!, default!, default, default);
        await _triggerService.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task EvaluateAsync_NonGuidUserId_FiresNothing()
    {
        await _sut.EvaluateAsync("not-a-guid", AgentTriggerKinds.UnstaffedShift);

        await _triggerService.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task EvaluateAsync_FourthDismissalAfterFiredSuggestion_DedupPreventsSecondQuestion()
    {
        var rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        var preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        var notificationService = Substitute.For<IAssistantNotificationService>();
        var activityTracker = Substitute.For<IUserActivityTracker>();
        var planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        notificationService.GetConnectedUserIdsAsync().Returns(new[] { UserId });
        _dispatchRepository
            .WasDispatchedAsync(UserId, AgentTriggerKinds.MuteSuggestion, "mute-suggestion:" + AgentTriggerKinds.UnstaffedShift, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        SetRecentReactions(
            ReactionRow(ProactiveReaction.Dismissed, 1),
            ReactionRow(ProactiveReaction.Dismissed, 2),
            ReactionRow(ProactiveReaction.Dismissed, 3));
        var triggerService = new AgentTriggerService(
            rateLimiter, preferenceService, notificationService, _dispatchRepository,
            Substitute.For<IAgentConditionRepository>(),
            activityTracker, planningAudienceResolver, Substitute.For<IOfflineMessengerNotifier>(),
            Substitute.For<IProactiveMessengerTextComposer>(),
            TimeProvider.System,
            NullLogger<AgentTriggerService>.Instance);
        var evaluator = new DismissStreakEvaluator(_dispatchRepository, triggerService);

        await evaluator.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
        await notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }
}
