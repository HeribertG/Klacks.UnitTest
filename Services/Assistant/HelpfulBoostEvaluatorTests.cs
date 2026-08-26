// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for HelpfulBoostEvaluator — verifies that the helpful reactions within the
/// configured lookback window are counted and handed to the rate limiter as the per-user
/// daily budget boost of the kind, that a window without helpful reactions clears the boost,
/// and that the mute_suggestion kind is excluded from helpful learning.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class HelpfulBoostEvaluatorTests
{
    private static readonly string UserId = Guid.NewGuid().ToString();

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private HelpfulBoostEvaluator _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        _sut = new HelpfulBoostEvaluator(_dispatchRepository, _rateLimiter);
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
    public async Task EvaluateAsync_MixedReactions_SetsBoostToHelpfulCount()
    {
        SetRecentReactions(
            ReactionRow(ProactiveReaction.Helpful, 1),
            ReactionRow(ProactiveReaction.Dismissed, 2),
            ReactionRow(ProactiveReaction.Helpful, 3));

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        _rateLimiter.Received(1).SetDailyBudgetBoost(UserId, AgentTriggerKinds.UnstaffedShift, 2);
    }

    [Test]
    public async Task EvaluateAsync_OnlyDismissals_ClearsBoost()
    {
        SetRecentReactions(
            ReactionRow(ProactiveReaction.Dismissed, 1),
            ReactionRow(ProactiveReaction.Dismissed, 2));

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        _rateLimiter.Received(1).SetDailyBudgetBoost(UserId, AgentTriggerKinds.UnstaffedShift, 0);
    }

    [Test]
    public async Task EvaluateAsync_EmptyReactionHistory_ClearsBoost()
    {
        SetRecentReactions();

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        _rateLimiter.Received(1).SetDailyBudgetBoost(UserId, AgentTriggerKinds.UnstaffedShift, 0);
    }

    [Test]
    public async Task EvaluateAsync_MuteSuggestionKind_IsExcludedFromHelpfulLearning()
    {
        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.MuteSuggestion);

        await _dispatchRepository.DidNotReceiveWithAnyArgs().GetRecentReactionsAsync(default!, default!, default, default);
        _rateLimiter.DidNotReceiveWithAnyArgs().SetDailyBudgetBoost(default!, default!, default);
    }

    [Test]
    public async Task EvaluateAsync_LoadsReactionsWithConfiguredLookbackWindow()
    {
        SetRecentReactions();

        await _sut.EvaluateAsync(UserId, AgentTriggerKinds.UnstaffedShift);

        await _dispatchRepository.Received(1).GetRecentReactionsAsync(
            UserId, AgentTriggerKinds.UnstaffedShift, ProactiveHelpfulLearning.RecentReactionsTake, Arg.Any<CancellationToken>());
    }
}
