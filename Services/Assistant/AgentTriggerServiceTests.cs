// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AgentTriggerService — verifies the severity routing matrix (high goes to the
/// live chat push, medium/low operational alerts only to the persisted inbox plus a lightweight
/// inbox-changed signal, companion events always reach the chat), that the recipient set derives
/// from the event audience so offline planners still get inbox rows, that preference, dedup and
/// rate-limit gates block before anything is persisted, that rows are persisted before the live
/// push so a failed push keeps the message reachable via the inbox, and that overlong summary
/// param values are capped so the persisted JSON never exceeds the database column limit.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AgentTriggerServiceTests
{
    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private IAgentTriggerPreferenceService _preferenceService = null!;
    private IAssistantNotificationService _notificationService = null!;
    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IUserActivityTracker _activityTracker = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private AgentTriggerService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        _preferenceService = Substitute.For<IAgentTriggerPreferenceService>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _activityTracker = Substitute.For<IUserActivityTracker>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _preferenceService.IsAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        // Default: the users used across the dispatch tests are planners, so the audience gate is a no-op there.
        SetPlanners("user-a", "user-b");
        _sut = new AgentTriggerService(_rateLimiter, _preferenceService, _notificationService,
            _dispatchRepository, _activityTracker, _planningAudienceResolver, NullLogger<AgentTriggerService>.Instance);
    }

    private void SetPlanners(params string[] userIds) =>
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(userIds, StringComparer.OrdinalIgnoreCase));

    private static UnstaffedShiftTriggerEvent MakeEvent(int daysUntil = 2) =>
        new(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysUntil)), daysUntil, null);

    private sealed record PlainBroadcastEvent(string SeverityValue, string SummaryText) : IAgentTriggerEvent
    {
        public string Kind => "test_plain";
        public string Severity => SeverityValue;
        public string Summary => SummaryText;
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
    }

    private sealed record ParamsBroadcastEvent(IReadOnlyDictionary<string, string> Params) : IAgentTriggerEvent
    {
        public string Kind => "test_params";
        public string Severity => AgentTriggerSeverity.Low;
        public string Summary => "Params summary.";
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
        public IReadOnlyDictionary<string, string>? SummaryParams => Params;
    }


    [Test]
    public async Task OnEventAsync_CompanionBroadcast_NoConnectedUsers_PersistsAndSendsNothing()
    {
        _notificationService.GetConnectedUserIds().Returns(Array.Empty<string>());

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_HighSeverity_ConnectedPlanners_LivePushesAndRecordsFire()
    {
        var users = new[] { "user-a", "user-b" };
        _notificationService.GetConnectedUserIds().Returns(users);
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _notificationService.Received(1).SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        _rateLimiter.Received(1).RecordFire("user-b", AgentTriggerKinds.UnstaffedShift);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_MediumSeverity_ConnectedPlanner_RecordsAndSignalsInboxWithoutChatPush()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _dispatchRepository.CountUnreadAsync("user-a", Arg.Any<CancellationToken>()).Returns(3);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 5));

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        await _notificationService.Received(1).SendProactiveInboxChangedAsync("user-a", 3);
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_OfflinePlanners_RecordsInboxRowsWithoutAnyPush()
    {
        _notificationService.GetConnectedUserIds().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        _rateLimiter.Received(1).RecordFire("user-b", AgentTriggerKinds.UnstaffedShift);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }

    [Test]
    public async Task OnEventAsync_RateLimited_BlocksThatUserBeforePersist()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire("user-a", Arg.Any<string>()).Returns(true);
        _rateLimiter.ShouldFire("user-b", Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_MutedByPreference_BlocksThatUserBeforePersist()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService.IsAllowedAsync("user-a", Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService.IsAllowedAsync("user-b", Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-b"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_AlreadyDispatched_DedupBlocksBeforePersist()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _dispatchRepository
            .WasDispatchedAsync("user-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.DidNotReceiveWithAnyArgs().RecordAsync(default!, default);
        _rateLimiter.DidNotReceiveWithAnyArgs().RecordFire(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_UserActiveInConversation_PersistsAndSignalsInboxInsteadOfChatPush()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        _activityTracker.IsRecentlyActive("user-a", Arg.Any<TimeSpan>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        await _notificationService.Received(1).SendProactiveInboxChangedAsync("user-a", Arg.Any<int>());
    }

    [Test]
    public async Task OnEventAsync_PlainHighSeverityEvent_PrefixesWithTag()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.High, "Plain summary."));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a",
            Arg.Is<string>(s => s.StartsWith("[HIGH]")),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task OnEventAsync_I18nEvent_SendsBareKeyWithoutTag_AndForwardsParams()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        // UnstaffedShift with daysUntil=1 is High severity, but is an i18n event.
        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a",
            Arg.Is<string>(s => s == ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.UnstaffedShift),
            Arg.Any<string?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(p => p != null && p.ContainsKey("date") && p.ContainsKey("days")),
            Arg.Any<string?>());
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_LivePushesOnlyToPlanners_NeverToConnectedEmployees()
    {
        const string planner = "planner-1";
        const string employee = "employee-1";
        _notificationService.GetConnectedUserIds().Returns(new[] { planner, employee });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners(planner);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            planner, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(
            employee, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == employee), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_OnlyEmployeesConnected_RecordsForOfflinePlannerWithoutPush()
    {
        const string offlinePlanner = "some-other-planner";
        _notificationService.GetConnectedUserIds().Returns(new[] { "employee-1", "employee-2" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners(offlinePlanner);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == offlinePlanner), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_CompanionBroadcast_LivePushesToConnectedUsersEvenAtLowSeverity()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "employee-1" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("some-other-planner");

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "employee-1", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "employee-1"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_Dispatch_RecordsContentKeySeverityAndCouplesMessageIdToRowId()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("user-a");
        string? sentMessageId = null;
        ProactiveTriggerDispatchRow? recordedRow = null;
        _notificationService
            .When(n => n.SendProactiveMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>()))
            .Do(ci => sentMessageId = ci.ArgAt<string?>(4));
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));

        var triggerEvent = MakeEvent();
        await _sut.OnEventAsync(triggerEvent);

        Assert.That(sentMessageId, Is.Not.Null.And.Not.Empty);
        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.Id.ToString(), Is.EqualTo(sentMessageId));
        Assert.That(recordedRow.UserId, Is.EqualTo("user-a"));
        Assert.That(recordedRow.TriggerKind, Is.EqualTo(triggerEvent.Kind));
        Assert.That(recordedRow.DedupKey, Is.EqualTo(triggerEvent.DedupKey));
        Assert.That(recordedRow.ContentKey, Is.EqualTo(triggerEvent.Summary));
        Assert.That(recordedRow.Severity, Is.EqualTo(triggerEvent.Severity));
        Assert.That(recordedRow.ContentParamsJson, Does.Contain("date"));
        Assert.That(recordedRow.Reaction, Is.EqualTo(ProactiveReaction.None));
        Assert.That(recordedRow.ReactionAtUtc, Is.Null);
    }

    [Test]
    public async Task OnEventAsync_OverlongParamValue_CapsValueAndKeepsJsonWithinColumnLimit()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var overlongValue = new string('x', 6000);

        await _sut.OnEventAsync(new ParamsBroadcastEvent(new Dictionary<string, string> { ["error"] = overlongValue }));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ContentParamsJson, Is.Not.Null);
        Assert.That(recordedRow.ContentParamsJson!.Length, Is.LessThanOrEqualTo(ProactiveTriggerDispatchLimits.ContentParamsJsonMaxLength));
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(recordedRow.ContentParamsJson);
        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!["error"], Has.Length.EqualTo(ProactiveTriggerDispatchLimits.ContentParamValueMaxLength));
        Assert.That(deserialized["error"], Does.EndWith(ProactiveTriggerDispatchLimits.TruncationSuffix));
    }

    [Test]
    public async Task OnEventAsync_ParamsExceedColumnLimitEvenAfterCapping_StoresNullInsteadOfInvalidJson()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        ProactiveTriggerDispatchRow? recordedRow = null;
        _dispatchRepository
            .When(r => r.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(ci => recordedRow = ci.ArgAt<ProactiveTriggerDispatchRow>(0));
        var manyParams = Enumerable.Range(0, 6)
            .ToDictionary(i => $"param{i}", i => new string('x', 900));

        await _sut.OnEventAsync(new ParamsBroadcastEvent(manyParams));

        Assert.That(recordedRow, Is.Not.Null);
        Assert.That(recordedRow!.ContentParamsJson, Is.Null);
        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task OnEventAsync_SendFails_RowStaysPersistedAndBudgetCounted_MessageReachableViaInbox()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _notificationService
            .SendProactiveMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>())
            .Returns(Task.FromException(new InvalidOperationException("send failed")));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == "user-a"), Arg.Any<CancellationToken>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
    }

    [Test]
    public async Task OnEventAsync_PersistFails_DoesNotCountBudgetOrPush()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _dispatchRepository
            .RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("persist failed")));

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        _rateLimiter.DidNotReceiveWithAnyArgs().RecordFire(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_TargetedCompanionEvent_LivePushesOnlyToConnectedTargetUser()
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        _notificationService.GetConnectedUserIds().Returns(new[] { target.ToString(), other.ToString() });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new CuriosityQuestionTriggerEvent("sport", target));

        await _notificationService.Received(1).SendProactiveMessageAsync(target.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(other.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>());
        await _dispatchRepository.DidNotReceive().RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == other.ToString()), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnEventAsync_TargetedCompanionEvent_OfflineTarget_RecordsInboxRowWithoutPush()
    {
        var target = Guid.NewGuid();
        _notificationService.GetConnectedUserIds().Returns(Array.Empty<string>());
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new CuriosityQuestionTriggerEvent("sport", target));

        await _dispatchRepository.Received(1).RecordAsync(Arg.Is<ProactiveTriggerDispatchRow>(r => r.UserId == target.ToString()), Arg.Any<CancellationToken>());
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveInboxChangedAsync(default!, default);
    }
}

[TestFixture]
public class OperationalTriggerEventDedupKeyTests
{
    // Regression guard: i18n summaries are identical per event type, so each event MUST override
    // DedupKey with discriminating fields — otherwise the second alert of the same kind is dropped.

    [Test]
    public void AllOperationalEvents_HaveDiscriminatingDedupKey_NotEqualToSummary()
    {
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var endDate = new DateOnly(2026, 6, 30);

        var eventA = new PeriodCloseDueTriggerEvent(groupA, "Group A", endDate, 3);
        var eventB = new PeriodCloseDueTriggerEvent(groupB, "Group B", endDate, 3);

        Assert.That(eventA.DedupKey, Is.Not.EqualTo(eventA.Summary));
        Assert.That(eventA.DedupKey, Is.Not.EqualTo(eventB.DedupKey));
    }

    [Test]
    public void OperationalEvents_DedupKeyIsStableAcrossChangingMagnitude()
    {
        var groupId = Guid.NewGuid();
        var endDate = new DateOnly(2026, 6, 30);

        // Same group + period end, only the days-until countdown differs → same dedup key (alert once).
        var threeDays = new PeriodCloseDueTriggerEvent(groupId, "Group", endDate, 3);
        var oneDay = new PeriodCloseDueTriggerEvent(groupId, "Group", endDate, 1);

        Assert.That(threeDays.DedupKey, Is.EqualTo(oneDay.DedupKey));
    }

    [Test]
    public void EachOperationalEventType_ProducesAnI18nSummary()
    {
        var drift = new TargetHoursDriftTriggerEvent(Guid.NewGuid(), "Jane", -170m, "2026-06");
        var period = new PeriodCloseDueTriggerEvent(Guid.NewGuid(), "GE", new DateOnly(2026, 6, 30), 3);
        var unstaffed = new UnstaffedShiftTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 6, 30), 2, null);
        var lockConflict = new LockConflictDetectedTriggerEvent(Guid.NewGuid(), new DateOnly(2026, 6, 30), 2, null);
        var scenario = new ScenarioPendingTriggerEvent(Guid.NewGuid(), 80, null, "GE");
        var contract = new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), "Jane", new DateOnly(2026, 6, 30), 5);

        foreach (var ev in new IAgentTriggerEvent[] { drift, period, unstaffed, lockConflict, scenario, contract })
        {
            Assert.That(ev.PlannersOnly, Is.True, $"{ev.Kind} must be planners-only");
            Assert.That(ev.Summary, Does.StartWith(ProactiveMessageMarkers.I18nPrefix), $"{ev.Kind} must use an i18n summary");
            Assert.That(ev.SummaryParams, Is.Not.Null.And.Not.Empty, $"{ev.Kind} must carry summary params");
        }
    }
}

[TestFixture]
public class AgentTriggerRateLimiterTests
{
    private AgentTriggerRateLimiter _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new AgentTriggerRateLimiter();
    }

    [Test]
    public void FreshUser_HasFullDailyBudget()
    {
        Assert.That(_sut.ShouldFire("user-a", "kind"), Is.True);
        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(5));
    }

    [Test]
    public void RecordFire_DecrementsBudget()
    {
        _sut.RecordFire("user-a", "kind");

        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(4));
    }

    [Test]
    public void ExceededBudget_ShouldFireReturnsFalse()
    {
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", "kind");
        }

        Assert.That(_sut.ShouldFire("user-a", "kind"), Is.False);
        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(0));
    }

    [Test]
    public void IndependentKindsHaveIndependentBudgets()
    {
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", "kind-1");
        }

        Assert.That(_sut.ShouldFire("user-a", "kind-1"), Is.False);
        Assert.That(_sut.ShouldFire("user-a", "kind-2"), Is.True);
    }

    [Test]
    public void CuriosityKind_IsCappedAtOnePerDay()
    {
        Assert.That(_sut.GetRemainingBudget("user-a", AgentTriggerKinds.CuriosityQuestion), Is.EqualTo(1));

        _sut.RecordFire("user-a", AgentTriggerKinds.CuriosityQuestion);

        Assert.That(_sut.ShouldFire("user-a", AgentTriggerKinds.CuriosityQuestion), Is.False);
        Assert.That(_sut.GetRemainingBudget("user-a", AgentTriggerKinds.CuriosityQuestion), Is.EqualTo(0));
    }
}
