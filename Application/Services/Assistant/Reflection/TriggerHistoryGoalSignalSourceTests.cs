// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TriggerHistoryGoalSignalSource — covers the (UserId, TriggerKind) aggregation,
/// the exclusion of self-referential/meta trigger kinds (CuriosityQuestion, MuteSuggestion) and the
/// empty-history path. IProactiveTriggerDispatchRepository is mocked; no database involved.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

[TestFixture]
public class TriggerHistoryGoalSignalSourceTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private TriggerHistoryGoalSignalSource _sut = null!;

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _sut = new TriggerHistoryGoalSignalSource(_dispatchRepository, NullLogger<TriggerHistoryGoalSignalSource>.Instance);
    }

    private static ProactiveTriggerDispatchRow Row(string userId, string triggerKind, DateTime createTimeUtc) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TriggerKind = triggerKind,
        DedupKey = Guid.NewGuid().ToString(),
        CreateTime = createTimeUtc
    };

    private void SetupRows(params ProactiveTriggerDispatchRow[] rows)
    {
        _dispatchRepository
            .GetSinceAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(rows);
    }

    [Test]
    public async Task CollectAsync_NoDispatchRows_ReturnsEmpty()
    {
        SetupRows();

        var signals = await _sut.CollectAsync();

        signals.ShouldBeEmpty();
    }

    [Test]
    public async Task CollectAsync_MultipleRowsSameUserAndKind_AggregatesIntoOneSignal()
    {
        var first = new DateTime(2026, 7, 20, 8, 0, 0, DateTimeKind.Utc);
        var middle = new DateTime(2026, 7, 22, 8, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 7, 25, 8, 0, 0, DateTimeKind.Utc);
        SetupRows(
            Row(UserA, AgentTriggerKinds.UnstaffedShift, first),
            Row(UserA, AgentTriggerKinds.UnstaffedShift, middle),
            Row(UserA, AgentTriggerKinds.UnstaffedShift, last));

        var signals = await _sut.CollectAsync();

        signals.Count.ShouldBe(1);
        var signal = signals[0];
        signal.UserId.ShouldBe(UserA);
        signal.Kind.ShouldBe(AgentTriggerKinds.UnstaffedShift);
        signal.OccurrenceCount.ShouldBe(3);
        signal.FirstSeenUtc.ShouldBe(first);
        signal.LastSeenUtc.ShouldBe(last);
    }

    [Test]
    public async Task CollectAsync_DifferentUsersOrKinds_ProducesSeparateSignals()
    {
        var now = DateTime.UtcNow;
        SetupRows(
            Row(UserA, AgentTriggerKinds.UnstaffedShift, now),
            Row(UserA, AgentTriggerKinds.LockConflict, now),
            Row(UserB, AgentTriggerKinds.UnstaffedShift, now));

        var signals = await _sut.CollectAsync();

        signals.Count.ShouldBe(3);
    }

    [Test]
    public async Task CollectAsync_CuriosityQuestionKind_IsExcluded()
    {
        SetupRows(Row(UserA, AgentTriggerKinds.CuriosityQuestion, DateTime.UtcNow));

        var signals = await _sut.CollectAsync();

        signals.ShouldBeEmpty();
    }

    [Test]
    public async Task CollectAsync_MuteSuggestionKind_IsExcluded()
    {
        SetupRows(Row(UserA, AgentTriggerKinds.MuteSuggestion, DateTime.UtcNow));

        var signals = await _sut.CollectAsync();

        signals.ShouldBeEmpty();
    }
}
