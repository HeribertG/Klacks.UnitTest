// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for TriggerHistoryGoalSignalSource — covers the (UserId, TriggerKind) aggregation,
/// the exclusion of self-referential/meta trigger kinds (CuriosityQuestion, MuteSuggestion), the
/// exclusion of kinds without a GoalTypeCatalog entry, the summary being built from the catalogue's
/// plain-language description rather than the trigger identifier, the empty-history path, and the open_order
/// measure: distinct reported company days (from the summary dedup keys), raised from three of seven days on.
/// IProactiveTriggerDispatchRepository is mocked; no database involved.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
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
        _sut = new TriggerHistoryGoalSignalSource(
            _dispatchRepository,
            new FixedCompanyClock(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
            NullLogger<TriggerHistoryGoalSignalSource>.Instance);
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

    [Test]
    public async Task CollectAsync_KindWithoutCatalogueEntry_IsExcluded()
    {
        SetupRows(Row(UserA, "some_future_trigger_kind", DateTime.UtcNow));

        var signals = await _sut.CollectAsync();

        signals.ShouldBeEmpty();
    }

    [Test]
    public async Task CollectAsync_Summary_UsesCatalogueDescriptionAndNotTheTriggerIdentifier()
    {
        SetupRows(Row(UserA, AgentTriggerKinds.TargetHoursDrift, DateTime.UtcNow));

        var signals = await _sut.CollectAsync();

        var signal = signals.ShouldHaveSingleItem();
        var definition = GoalTypeCatalog.ByTriggerKind[AgentTriggerKinds.TargetHoursDrift];
        signal.Summary.ShouldContain(definition.SignalDescription);
        signal.Summary.ShouldNotContain(AgentTriggerKinds.TargetHoursDrift);
        signal.LookbackDays.ShouldBeGreaterThan(0);
        signal.Summary.ShouldContain(signal.LookbackDays.ToString());
    }

    private static ProactiveTriggerDispatchRow OpenOrderRow(string userId, string dedupKey, DateTime createTimeUtc)
    {
        var row = Row(userId, AgentTriggerKinds.OpenOrder, createTimeUtc);
        row.DedupKey = dedupKey;
        return row;
    }

    [Test]
    public async Task CollectAsync_OpenOrder_CountsDistinctReportedCompanyDays_NotMessages()
    {
        var day = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);
        SetupRows(
            OpenOrderRow(UserA, "2026-10-18#5", day),
            OpenOrderRow(UserA, "2026-10-18#7", day.AddHours(5)),
            OpenOrderRow(UserA, "2026-10-19#7", day.AddDays(1)),
            OpenOrderRow(UserA, "2026-10-21#4", day.AddDays(3)),
            OpenOrderRow(UserA, "2026-10-21#9", day.AddDays(3).AddHours(2)));

        var signal = (await _sut.CollectAsync()).ShouldHaveSingleItem();

        signal.Kind.ShouldBe(AgentTriggerKinds.OpenOrder);
        signal.OccurrenceCount.ShouldBe(3, "Three distinct company days, although five summaries were delivered.");
        signal.LookbackDays.ShouldBe(7);
        signal.Summary.ShouldContain("3 of the last 7 days");
        signal.Summary.ShouldNotContain(AgentTriggerKinds.OpenOrder);
    }

    [Test]
    public async Task CollectAsync_OpenOrder_OnFewerThanThreeDays_RaisesNoSignal()
    {
        var day = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);
        SetupRows(
            OpenOrderRow(UserA, "2026-10-18#5", day),
            OpenOrderRow(UserA, "2026-10-18#6", day.AddHours(1)),
            OpenOrderRow(UserA, "2026-10-18#8", day.AddHours(2)),
            OpenOrderRow(UserA, "2026-10-20#2", day.AddDays(2)));

        var signals = await _sut.CollectAsync();

        signals.ShouldBeEmpty();
    }

    [Test]
    public async Task CollectAsync_OpenOrder_IgnoresRowsOfTheFormerPerOrderShape()
    {
        var day = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);
        SetupRows(
            OpenOrderRow(UserA, $"{Guid.NewGuid()}:2026-10-25", day),
            OpenOrderRow(UserA, $"{Guid.NewGuid()}:2026-10-26", day.AddDays(1)),
            OpenOrderRow(UserA, $"{Guid.NewGuid()}:2026-10-27", day.AddDays(2)),
            OpenOrderRow(UserA, "2026-10-21#3", day.AddDays(3)));

        var signals = await _sut.CollectAsync();

        signals.ShouldBeEmpty();
    }

    [Test]
    public async Task CollectAsync_OpenOrder_CountsOnlyTheLastSevenCompanyDays_NeverMoreThanSeven()
    {
        // Company today is 2026-10-22, so the counted days are 16.10. to 22.10.; a summary of 15.10. (eighth day back,
        // still inside a 7x24h UTC row window) and one of a future day must not count.
        var morning = new DateTime(2026, 10, 15, 7, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 8)
            .Select(offset => OpenOrderRow(UserA, $"2026-10-{15 + offset}#4", morning.AddDays(offset)))
            .Append(OpenOrderRow(UserA, "2026-10-23#4", morning.AddDays(7)))
            .ToArray();
        SetupRows(rows);

        var signal = (await _sut.CollectAsync()).ShouldHaveSingleItem();

        signal.OccurrenceCount.ShouldBe(7);
        signal.LookbackDays.ShouldBe(7);
    }

    [Test]
    public async Task CollectAsync_OpenOrder_IsCountedPerRecipient()
    {
        var day = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);
        SetupRows(
            OpenOrderRow(UserA, "2026-10-18#5", day),
            OpenOrderRow(UserA, "2026-10-19#5", day.AddDays(1)),
            OpenOrderRow(UserA, "2026-10-20#5", day.AddDays(2)),
            OpenOrderRow(UserB, "2026-10-18#1", day),
            OpenOrderRow(UserB, "2026-10-19#1", day.AddDays(1)));

        var signal = (await _sut.CollectAsync()).ShouldHaveSingleItem();

        signal.UserId.ShouldBe(UserA);
    }
}
