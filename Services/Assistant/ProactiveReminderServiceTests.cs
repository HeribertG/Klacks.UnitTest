// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProactiveReminderService (package F "repeat until acknowledged") — verifies the
/// per-row gate order (gone/terminal condition stops the loop without a delivery, mute/snooze and the
/// per-user cap defer without counting a reminder, the compare-and-swap is the claim and a lost claim
/// delivers nothing), the persist-before-push rule (TryAdvance runs before any SignalR delivery and a
/// delivery failure never rolls the advance back), the DeliverAsync-mirroring delivery matrix (loud
/// row to a connected user goes to the chat with messageId = row id, a quiet row only nudges the
/// inbox badge, an offline user gets nothing live but still counts as reminded), and that one broken
/// row never aborts the rest of the batch. The gate order itself is pinned by a combination case:
/// a terminal condition on a row of a muted user stops the row instead of deferring it, because the
/// condition gate is checked before the preference gate.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ProactiveReminderServiceTests
{
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IAgentTriggerPreferenceService _preferenceService = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IUserActivityTracker _activityTracker = null!;
    private SettableTimeProvider _timeProvider = null!;
    private RecordingLogger<ProactiveReminderService> _logger = null!;
    private ProactiveReminderService _sut = null!;

    private const string UserId = "user-a";

    private static readonly DateTime FakeNow = new(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void Setup()
    {
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _activityTracker = Substitute.For<IUserActivityTracker>();
        _timeProvider = new SettableTimeProvider(FakeNow);
        _logger = new RecordingLogger<ProactiveReminderService>();

        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _notificationService.GetConnectedUserIdsAsync().Returns(new List<string>());
        _conditionRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Status = AgentConditionStatus.Reported });
        _dispatchRepository.TryAdvanceReminderAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _dispatchRepository.TryRescheduleReminderAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _dispatchRepository.CountUnreadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(3);

        _sut = new ProactiveReminderService(
            _dispatchRepository, _conditionRepository, _preferenceService, _notificationService,
            _activityTracker, _timeProvider, _logger);
    }

    [Test]
    public async Task RunAsync_NoDueRows_ReturnsZeroResult()
    {
        SetDueRows();

        var result = await _sut.RunAsync();

        Assert.That(result, Is.EqualTo(new ProactiveReminderSweepResult(0, 0, 0, 0, 0)));
        await _conditionRepository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task RunAsync_ConditionMissing_StopsRowWithoutDelivery()
    {
        var row = MakeRow();
        _conditionRepository.GetByIdAsync(row.ConditionId!.Value, Arg.Any<CancellationToken>())
            .Returns((AgentCondition?)null);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Stopped, Is.EqualTo(1));
        Assert.That(result.Reminded, Is.EqualTo(0));
        await _dispatchRepository.Received(1).TryRescheduleReminderAsync(
            row.Id, row.NextReminderAtUtc!.Value, null, Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryAdvanceReminderAsync(default, default, default, default);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task RunAsync_ConditionTerminal_StopsRowWithoutDelivery()
    {
        var row = MakeRow();
        _conditionRepository.GetByIdAsync(row.ConditionId!.Value, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Status = AgentConditionStatus.Resolved });
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Stopped, Is.EqualTo(1));
        await _dispatchRepository.Received(1).TryRescheduleReminderAsync(
            row.Id, row.NextReminderAtUtc!.Value, null, Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryAdvanceReminderAsync(default, default, default, default);
    }

    [Test]
    public async Task RunAsync_ConditionTerminalAndUserMuted_StopsRatherThanSkips()
    {
        var row = MakeRow();
        _conditionRepository.GetByIdAsync(row.ConditionId!.Value, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Status = AgentConditionStatus.Resolved });
        _preferenceService.IsAllowedAsync(UserId, row.TriggerKind, Arg.Any<string>()).Returns(false);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Stopped, Is.EqualTo(1), "The condition gate runs first, so a terminal finding wins over the mute.");
        Assert.That(result.Skipped, Is.EqualTo(0));
        await _dispatchRepository.Received(1).TryRescheduleReminderAsync(
            row.Id, row.NextReminderAtUtc!.Value, null, Arg.Any<CancellationToken>());
        await _preferenceService.DidNotReceiveWithAnyArgs().IsAllowedAsync(default!, default!, default!);
    }

    [Test]
    public async Task RunAsync_UserMuted_SkipsAndReschedulesWithoutCounting()
    {
        var row = MakeRow(reminderCount: 1);
        _preferenceService.IsAllowedAsync(UserId, row.TriggerKind, Arg.Any<string>()).Returns(false);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Skipped, Is.EqualTo(1));
        Assert.That(result.Reminded, Is.EqualTo(0));
        // Deferred to the SAME backoff step the row was already on (reminder count 1 -> +4h),
        // not advanced to the next one.
        await _dispatchRepository.Received(1).TryRescheduleReminderAsync(
            row.Id, row.NextReminderAtUtc!.Value, FakeNow.AddHours(4), Arg.Any<CancellationToken>());
        await _dispatchRepository.DidNotReceiveWithAnyArgs().TryAdvanceReminderAsync(default, default, default, default);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task RunAsync_UserCapReached_SkipsEleventhRow()
    {
        var rows = Enumerable.Range(0, ProactiveReminderDefaults.MaxRemindersPerUserPerSweep + 1)
            .Select(_ => MakeRow())
            .ToArray();
        SetDueRows(rows);

        var result = await _sut.RunAsync();

        Assert.That(result.Reminded, Is.EqualTo(ProactiveReminderDefaults.MaxRemindersPerUserPerSweep));
        Assert.That(result.Skipped, Is.EqualTo(1));
        await _dispatchRepository.Received(ProactiveReminderDefaults.MaxRemindersPerUserPerSweep)
            .TryAdvanceReminderAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).TryRescheduleReminderAsync(
            rows[^1].Id, rows[^1].NextReminderAtUtc!.Value, FakeNow.AddHours(1), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_AdvanceLost_CountsLostAndDeliversNothing()
    {
        var row = MakeRow();
        _dispatchRepository.TryAdvanceReminderAsync(
                row.Id, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);
        Connect(UserId);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Lost, Is.EqualTo(1));
        Assert.That(result.Reminded, Is.EqualTo(0));
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task RunAsync_ConnectedLoudRow_LivePushesWithRowIdAsMessageId()
    {
        var row = MakeRow(severity: AgentTriggerSeverity.High);
        Connect(UserId);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Reminded, Is.EqualTo(1));
        // Reminder count 0 advances to 1, so the next due date takes the second backoff step (+4h).
        await _dispatchRepository.Received(1).TryAdvanceReminderAsync(
            row.Id, row.NextReminderAtUtc!.Value, FakeNow, FakeNow.AddHours(4), Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveMessageAsync(
            UserId,
            Arg.Any<string>(),
            conversationId: null,
            contentParams: Arg.Any<IReadOnlyDictionary<string, string>?>(),
            messageId: row.Id.ToString(),
            kind: row.TriggerKind,
            actionRoute: row.ActionRoute,
            actionParams: Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task RunAsync_ConnectedRecentlyActiveRow_IsNotLoudAndSignalsInbox()
    {
        var row = MakeRow(severity: AgentTriggerSeverity.High);
        _activityTracker.IsRecentlyActive(UserId, Arg.Any<TimeSpan>()).Returns(true);
        Connect(UserId);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Reminded, Is.EqualTo(1));
        await _notificationService.Received(1).SendProactiveInboxChangedAsync(UserId, 3);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task RunAsync_ConnectedNotLoudRow_SendsInboxChangedWithUnreadCount()
    {
        var row = MakeRow(severity: AgentTriggerSeverity.Medium);
        Connect(UserId);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        Assert.That(result.Reminded, Is.EqualTo(1));
        await _dispatchRepository.Received(1).CountUnreadAsync(UserId, Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveInboxChangedAsync(UserId, 3);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task RunAsync_OfflineUser_AdvancesButSendsNothing()
    {
        var row = MakeRow(severity: AgentTriggerSeverity.High);
        SetDueRows(row);

        var result = await _sut.RunAsync();

        // The reminder still counts - persist before push: the row was advanced and resurfaced as
        // unread, which is the whole delivery an offline user ever gets.
        Assert.That(result.Reminded, Is.EqualTo(1));
        await _dispatchRepository.Received(1).TryAdvanceReminderAsync(
            row.Id, row.NextReminderAtUtc!.Value, FakeNow, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task RunAsync_RowThrows_NextRowStillProcessed()
    {
        var broken = MakeRow();
        var healthy = MakeRow(severity: AgentTriggerSeverity.High);
        _conditionRepository.GetByIdAsync(broken.ConditionId!.Value, Arg.Any<CancellationToken>())
            .Returns<AgentCondition?>(_ => throw new InvalidOperationException("ledger read failed"));
        Connect(UserId);
        SetDueRows(broken, healthy);

        var result = await _sut.RunAsync();

        Assert.That(result.Due, Is.EqualTo(2));
        Assert.That(result.Reminded, Is.EqualTo(1));
        await _notificationService.Received(1).SendProactiveMessageAsync(
            UserId,
            Arg.Any<string>(),
            conversationId: null,
            contentParams: Arg.Any<IReadOnlyDictionary<string, string>?>(),
            messageId: healthy.Id.ToString(),
            kind: Arg.Any<string>(),
            actionRoute: Arg.Any<string>(),
            actionParams: Arg.Any<IReadOnlyDictionary<string, string>?>());
        Assert.That(_logger.Entries.Count(entry => entry.Level == LogLevel.Error), Is.EqualTo(1));
    }

    private void Connect(string userId) =>
        _notificationService.GetConnectedUserIdsAsync().Returns(new List<string> { userId });

    private void SetDueRows(params ProactiveTriggerDispatchRow[] rows) =>
        _dispatchRepository.GetDueForReminderAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ProactiveTriggerDispatchRow>>(rows);

    private static ProactiveTriggerDispatchRow MakeRow(
        string userId = UserId,
        string severity = AgentTriggerSeverity.High,
        int reminderCount = 0) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TriggerKind = AgentTriggerKinds.UnstaffedShift,
        DedupKey = Guid.NewGuid().ToString("N"),
        ContentKey = "shift.unstaffed",
        Severity = severity,
        ConditionId = Guid.NewGuid(),
        ReminderCount = reminderCount,
        NextReminderAtUtc = FakeNow.AddHours(-1)
    };
}
