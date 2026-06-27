// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AgentTriggerService — verifies notification dispatch per connected user,
/// rate-limit gating, severity-tag prefix and graceful handling when no users are connected.
/// </summary>

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

    [Test]
    public async Task OnEventAsync_NoConnectedUsers_SkipsDispatch()
    {
        _notificationService.GetConnectedUserIds().Returns(Array.Empty<string>());

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_DispatchesToEachConnectedUser_AndRecordsFire()
    {
        var users = new[] { "user-a", "user-b" };
        _notificationService.GetConnectedUserIds().Returns(users);
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.Received(1).SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        _rateLimiter.Received(1).RecordFire("user-b", AgentTriggerKinds.UnstaffedShift);
    }

    [Test]
    public async Task OnEventAsync_RateLimited_SkipsThatUser()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire("user-a", Arg.Any<string>()).Returns(true);
        _rateLimiter.ShouldFire("user-b", Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_MutedByPreference_SkipsThatUser()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService.IsAllowedAsync("user-a", Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _preferenceService.IsAllowedAsync("user-b", Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_AlreadyDispatched_SkipsDedup()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _dispatchRepository
            .WasDispatchedAsync("user-a", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_UserActiveInConversation_SkipsThatUser()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _activityTracker.IsRecentlyActive("user-a", Arg.Any<TimeSpan>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
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
            Arg.Any<IReadOnlyDictionary<string, string>?>());
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
            Arg.Is<IReadOnlyDictionary<string, string>?>(p => p != null && p.ContainsKey("date") && p.ContainsKey("days")));
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_DeliversOnlyToConnectedPlanners()
    {
        const string planner = "planner-1";
        const string employee = "employee-1";
        _notificationService.GetConnectedUserIds().Returns(new[] { planner, employee });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners(planner);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.Received(1).SendProactiveMessageAsync(
            planner, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(
            employee, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_PlannersOnly_NoConnectedPlanner_SkipsDispatch()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "employee-1", "employee-2" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("some-other-planner");

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_BroadcastEvent_IgnoresAudienceGate()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "employee-1" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        SetPlanners("some-other-planner");

        await _sut.OnEventAsync(new PlainBroadcastEvent(AgentTriggerSeverity.Low, "Broadcast."));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "employee-1", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task OnEventAsync_TargetedEvent_DispatchesOnlyToTargetUser()
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        _notificationService.GetConnectedUserIds().Returns(new[] { target.ToString(), other.ToString() });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(new CuriosityQuestionTriggerEvent("sport", target));

        await _notificationService.Received(1).SendProactiveMessageAsync(target.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync(other.ToString(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
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
