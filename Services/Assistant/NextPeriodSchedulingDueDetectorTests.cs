// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NextPeriodSchedulingDueDetector — covers the autonomy gating branch (hint below
/// Autonomous, automatic AutoWizard start from Autonomous upwards, MIN aggregation over admins),
/// the Individual-interval skip, the email-backlog gate, the scenario-already-exists skip and the
/// interrupted auto-commit an API restart leaves behind, plus the governance brakes the detector no
/// longer checks itself (kind disabled, MaxAction below Prepare, MaxAction Prepare without a commit
/// intent). The autonomy aggregation runs through the REAL NextPeriodAutonomyResolver and the REAL
/// AdminAutonomyLevelAggregator rather than substitutes: they are the pieces the detector shares with
/// the auto-commit watcher, and stubbing them here would stop testing the rule the tests are named
/// after.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using AppSettings = Klacks.Api.Application.Constants.Settings;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodSchedulingDueDetectorTests
{
    private const string AdminId = "3f1c9a52-0000-0000-0000-000000000001";
    private const string SecondAdminId = "3f1c9a52-0000-0000-0000-000000000002";
    private static readonly DateTime NowUtc = new(2026, 1, 28, 9, 0, 0, DateTimeKind.Utc);

    private IGroupRepository _groupRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IScheduleActivityProbe _activityProbe = null!;
    private IAutoWizardJobRunner _autoWizardJobRunner = null!;
    private IClientRepository _clientRepository = null!;
    private IShiftScheduleRepository _shiftScheduleRepository = null!;
    private INextPeriodAutoCommitService _autoCommitService = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentAutonomyPreferenceRepository _autonomyPreferences = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private ISettingsReader _settingsReader = null!;
    private IReceivedEmailRepository _receivedEmailRepository = null!;
    private SettableTimeProvider _timeProvider = null!;
    private NextPeriodSchedulingDueDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _activityProbe = Substitute.For<IScheduleActivityProbe>();
        _activityProbe.HasPlannableShiftsInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _autoWizardJobRunner = Substitute.For<IAutoWizardJobRunner>();
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftScheduleRepository = Substitute.For<IShiftScheduleRepository>();
        _autoCommitService = Substitute.For<INextPeriodAutoCommitService>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _autonomyPreferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _conditionRepository.GetOpenByKindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentCondition>());
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _receivedEmailRepository = Substitute.For<IReceivedEmailRepository>();
        _timeProvider = new SettableTimeProvider(NowUtc);

        StubWeekStart(DayOfWeek.Monday);
        StubEmailAnalysis(enabled: false, backlogCount: 0);
        StubScenarios();
        StubAdmins((AdminId, AutonomyLevel.Propose));
        StubAgentsAndShifts();
        StubKillSwitch(active: false);

        _sut = CreateSut(new DateOnly(2026, 1, 28));
    }

    private NextPeriodSchedulingDueDetector CreateSut(DateOnly today)
    {
        var clock = new FixedCompanyClock(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new NextPeriodSchedulingDueDetector(
            _groupRepository,
            _weekConfiguration,
            _scenarioRepository,
            _activityProbe,
            _autoWizardJobRunner,
            _clientRepository,
            _shiftScheduleRepository,
            _autoCommitService,
            CreateAutonomyResolver(),
            _conditionRepository,
            _settingsReader,
            _receivedEmailRepository,
            NullLogger<NextPeriodSchedulingDueDetector>.Instance,
            clock,
            _timeProvider);
    }

    private NextPeriodAutonomyResolver CreateAutonomyResolver() =>
        new(new AdminAutonomyLevelAggregator(_audienceResolver, _autonomyPreferences), _governanceResolver);

    private void StubKillSwitch(bool active)
    {
        StubGovernance(ProactiveMaxAction.Execute, enabled: true, killSwitchActive: active);

        // Default: no global cap, so the per-admin aggregation alone decides; a test that cares
        // overrides this with a lower level.
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);
    }

    /// <summary>
    /// The governance row of this kind, shaped the way ProactiveGovernanceResolver would shape it: the
    /// kill switch and a disabled kind pin EffectiveMaxAction to Hint. The global-level cap is NOT folded
    /// in here - a test that lowers the global level asserts through the raw level instead, which is the
    /// stricter of the two paths.
    /// </summary>
    private void StubGovernance(ProactiveMaxAction configuredMaxAction, bool enabled, bool killSwitchActive)
    {
        var effective = killSwitchActive || !enabled ? ProactiveMaxAction.Hint : configuredMaxAction;
        _governanceResolver.ResolveAsync(
                AgentTriggerKinds.NextPeriodSchedulingDue, null, Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: AgentTriggerKinds.NextPeriodSchedulingDue,
                GroupId: null,
                EffectiveMaxAction: effective,
                ConfiguredMaxAction: configuredMaxAction,
                Enabled: enabled,
                KillSwitchActive: killSwitchActive,
                DailyActionBudget: ProactiveGovernanceDefaults.DailyActionBudget,
                WindowActionLimit: ProactiveGovernanceDefaults.WindowActionLimit,
                WindowMinutes: ProactiveGovernanceDefaults.WindowMinutes,
                IsStored: true,
                GlobalAutonomyCap: ProactiveMaxAction.Execute));
    }

    private void StubWeekStart(DayOfWeek weekStartDay)
    {
        _weekConfiguration.GetWeekStartAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var date = ci.Arg<DateOnly>();
                var offset = ((int)date.DayOfWeek - (int)weekStartDay + 7) % 7;
                return date.AddDays(-offset);
            });
    }

    private void StubGroups(params Group[] groups)
    {
        _groupRepository.List().Returns(groups.ToList());
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(groups.Select(group => group.Id).ToList());
    }

    private void StubEmailAnalysis(bool enabled, int backlogCount)
    {
        _settingsReader.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(Task.FromResult<SettingsRow?>(
                new SettingsRow { Type = AppSettings.EMAIL_ANALYSIS_ENABLED, Value = enabled.ToString() }));
        _receivedEmailRepository.GetUnprocessedAsync(Arg.Any<int>())
            .Returns(Enumerable.Range(0, backlogCount).Select(_ => new ReceivedEmail()).ToList());
    }

    private void StubScenarios(params AnalyseScenario[] scenarios)
    {
        _scenarioRepository.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(scenarios.ToList());
    }

    private void StubAdmins(params (string UserId, AutonomyLevel Level)[] admins)
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(admins.Select(admin => admin.UserId).ToHashSet());
        foreach (var (userId, level) in admins)
        {
            _autonomyPreferences.GetAsync(userId, Arg.Any<CancellationToken>())
                .Returns(new AgentAutonomyPreferenceRow { UserId = userId, Level = level });
        }
    }

    private void StubAgentsAndShifts(int agentCount = 1, int shiftCount = 1)
    {
        _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, agentCount).Select(_ => new Client { Id = Guid.NewGuid() }).ToList());
        _shiftScheduleRepository.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((Enumerable.Range(0, shiftCount).Select(_ => new ShiftDayAssignment { ShiftId = Guid.NewGuid() }).ToList(), shiftCount));
    }

    private static Group MakeGroup(PaymentInterval interval, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = DateTime.UtcNow.Date
    };

    [Test]
    public async Task DetectAsync_NoGroups_ReturnsEmpty()
    {
        StubGroups();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_IndividualInterval_AlwaysSkipped()
    {
        StubGroups(MakeGroup(PaymentInterval.Individual));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_OutsideLeadTime_Skips()
    {
        // 2026-01-10 is 22 days before the next period start (2026-02-01) — outside LeadTimeDays.
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_EmailBacklogWithAnalysisEnabled_DefersWholeScan()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubEmailAnalysis(enabled: true, backlogCount: 1);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _scenarioRepository.DidNotReceiveWithAnyArgs().GetByGroupAsync(default, default);
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_EmailAnalysisDisabled_BacklogIsNeverProbed()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubEmailAnalysis(enabled: false, backlogCount: 1);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        await _receivedEmailRepository.DidNotReceiveWithAnyArgs().GetUnprocessedAsync(default);
    }

    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    public async Task DetectAsync_BelowAutonomous_EmitsHintAndNeverStartsAutofill(AutonomyLevel level)
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, level));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var hint = (NextPeriodSchedulingDueTriggerEvent)events[0];
        Assert.That(hint.PeriodStartDate, Is.EqualTo(new DateOnly(2026, 2, 1)));
        Assert.That(hint.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 2, 28)));
        Assert.That(hint.DaysUntilStart, Is.EqualTo(4));
        Assert.That(hint.Kind, Is.EqualTo(AgentTriggerKinds.NextPeriodSchedulingDue));
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [TestCase(AutonomyLevel.Autonomous)]
    [TestCase(AutonomyLevel.FullyAutonomous)]
    public async Task DetectAsync_AutonomousOrHigher_StartsAutofillAndEmitsInfoEvent(AutonomyLevel level)
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubAdmins((AdminId, level));
        var jobId = Guid.NewGuid();
        _autoWizardJobRunner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var started = (NextPeriodAutofillStartedTriggerEvent)events[0];
        Assert.That(started.JobId, Is.EqualTo(jobId));
        Assert.That(started.GroupId, Is.EqualTo(group.Id));
        Assert.That(started.AutoCommitIntended, Is.EqualTo(level == AutonomyLevel.FullyAutonomous));
        await _autoWizardJobRunner.Received(1).StartAsync(
            Arg.Is<StartAutoWizardRequest>(request =>
                request.GroupId == group.Id
                && request.PeriodFrom == new DateOnly(2026, 2, 1)
                && request.PeriodUntil == new DateOnly(2026, 2, 28)),
            Arg.Any<CancellationToken>());

        if (level == AutonomyLevel.FullyAutonomous)
        {
            _autoCommitService.Received(1).QueueAutoCommit(
                jobId, group.Id, group.Name, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        }
        else
        {
            _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
        }
    }

    [TestCase(AutonomyLevel.Autonomous)]
    [TestCase(AutonomyLevel.FullyAutonomous)]
    public async Task DetectAsync_KillSwitchActive_FallsBackToHintEvenAtAutonomousOrHigher(AutonomyLevel level)
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, level));
        StubKillSwitch(active: true);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
    }

    [Test]
    public async Task DetectAsync_GovernanceMaxActionHint_FallsBackToHintEvenAtFullAutonomy()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Hint, enabled: true, killSwitchActive: false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>(),
            "The hint branch is never gated - a governance row that forbids acting must still report.");
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
    }

    [Test]
    public async Task DetectAsync_GovernanceKindDisabled_FallsBackToHintEvenAtFullAutonomy()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Execute, enabled: false, killSwitchActive: false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_GovernanceMaxActionPrepare_StartsAutofillWithoutACommitIntent()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Prepare, enabled: true, killSwitchActive: false);
        var jobId = Guid.NewGuid();
        _autoWizardJobRunner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns(jobId);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var started = (NextPeriodAutofillStartedTriggerEvent)events[0];
        Assert.That(started.AutoCommitIntended, Is.False,
            "Prepare lays a draft in front of a human; governance may lower the admin consent, never raise it.");
        _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
    }

    [Test]
    public async Task DetectAsync_MinAggregation_OneCautiousAdminBlocksAutoStart()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, AutonomyLevel.Propose));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_NoAdmins_DegradesToHint()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_GlobalAutonomyLevelBelowAutonomous_BlocksAutoStartDespiteAdminLevel()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.Autonomous));
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Propose);

        var events = await _sut.DetectAsync();

        // The installation-wide cap throttles the automatic start: at global level 0 the detector
        // falls back to the hint, no matter what the admins have chosen.
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_ScenarioAlreadyCoversNextPeriod_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            FromDate = new DateOnly(2026, 2, 1),
            UntilDate = new DateOnly(2026, 2, 28),
            Status = AnalyseScenarioStatus.Active
        });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_RejectedScenario_DoesNotCountAsPlanned()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            FromDate = new DateOnly(2026, 2, 1),
            UntilDate = new DateOnly(2026, 2, 28),
            Status = AnalyseScenarioStatus.Rejected
        });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
    }

    [Test]
    public async Task DetectAsync_AutofillRunAlreadyInProgress_EmitsNothingForThatGroup()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.Autonomous));
        _autoWizardJobRunner.StartAsync(Arg.Any<StartAutoWizardRequest>(), Arg.Any<CancellationToken>())
            .Returns<Guid>(_ => throw new AutofillRunConflictException(Guid.NewGuid(), AutofillFamily.AutoWizard));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AutonomousButNoShifts_FallsBackToHint()
    {
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        StubAdmins((AdminId, AutonomyLevel.FullyAutonomous));
        StubAgentsAndShifts(agentCount: 1, shiftCount: 0);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0], Is.TypeOf<NextPeriodSchedulingDueTriggerEvent>());
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_NextPeriodIsTheComingWeek()
    {
        // Today 2026-01-28 is a Wednesday; with a Monday week start the next period is Mo 2026-02-02
        // to Su 2026-02-08, five days ahead and therefore inside the lead window.
        StubGroups(MakeGroup(PaymentInterval.Weekly));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var hint = (NextPeriodSchedulingDueTriggerEvent)events[0];
        Assert.That(hint.PeriodStartDate, Is.EqualTo(new DateOnly(2026, 2, 2)));
        Assert.That(hint.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 2, 8)));
        Assert.That(hint.DaysUntilStart, Is.EqualTo(5));
    }

    [Test]
    public async Task DetectAsync_GroupWithoutClientsOrShifts_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _groupRepository.GetGroupIdsWithMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Guid>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). Next period start (2026-07-01) is the same
        // either way, so DaysUntilStart distinguishes the two: 4 under (wrong) UTC day, 3 under the
        // (correct) company day.
        StubGroups(MakeGroup(PaymentInterval.Monthly));
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new NextPeriodSchedulingDueDetector(
            _groupRepository, _weekConfiguration, _scenarioRepository, _activityProbe,
            _autoWizardJobRunner, _clientRepository, _shiftScheduleRepository, _autoCommitService,
            CreateAutonomyResolver(), _conditionRepository, _settingsReader,
            _receivedEmailRepository, NullLogger<NextPeriodSchedulingDueDetector>.Instance, clock,
            _timeProvider);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var hint = (NextPeriodSchedulingDueTriggerEvent)events[0];
        Assert.That(hint.PeriodStartDate, Is.EqualTo(new DateOnly(2026, 7, 1)));
        Assert.That(hint.DaysUntilStart, Is.EqualTo(3),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }

    [Test]
    public async Task DetectAsync_NextPeriodWithoutPlannableShift_EmitsNothing()
    {
        _activityProbe.HasPlannableShiftsInRangeAsync(Arg.Any<Group>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(false);
        StubGroups(MakeGroup(PaymentInterval.Monthly));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    private static AnalyseScenario ActiveFebruaryScenario(Guid groupId) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = groupId,
        FromDate = new DateOnly(2026, 2, 1),
        UntilDate = new DateOnly(2026, 2, 28),
        Status = AnalyseScenarioStatus.Active
    };

    /// <summary>
    /// The ledger row the tick writes for an automatic start, built from the real event so the payload
    /// the detector reads back is the one production actually stores.
    /// </summary>
    private static AgentCondition AutofillLedgerRow(
        Guid groupId, Guid jobId, bool autoCommitIntended, DateTime detectedAtUtc)
    {
        var startedEvent = new NextPeriodAutofillStartedTriggerEvent(
            groupId, "Bern", new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), jobId, autoCommitIntended);

        return new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = AgentTriggerKinds.NextPeriodSchedulingDue,
            Fingerprint = AgentConditionLedgerPolicy.FingerprintFor(startedEvent),
            GroupId = groupId,
            Status = AgentConditionStatus.Reported,
            DetectedAtUtc = detectedAtUtc,
            PayloadJson = JsonSerializer.Serialize(startedEvent.Payload)
        };
    }

    private static AgentCondition OutcomeLedgerRow(NextPeriodAutoCommitBlockedTriggerEvent blocked) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = AgentTriggerKinds.NextPeriodSchedulingDue,
        Fingerprint = AgentConditionLedgerPolicy.FingerprintFor(blocked),
        GroupId = blocked.GroupId,
        Status = AgentConditionStatus.Reported,
        DetectedAtUtc = BeyondGrace
    };

    private void StubOpenConditions(params AgentCondition[] rows) =>
        _conditionRepository.GetOpenByKindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(rows.ToList());

    private static DateTime BeyondGrace =>
        NowUtc.AddMinutes(-NextPeriodScheduling.AutoCommitInterruptedGraceMinutes - 1);

    [Test]
    public async Task DetectAsync_AutoCommitWatcherGoneAndDraftUnaccepted_ReportsInterruptedOnce()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        var scenario = ActiveFebruaryScenario(group.Id);
        StubScenarios(scenario);
        var jobId = Guid.NewGuid();
        StubOpenConditions(AutofillLedgerRow(group.Id, jobId, autoCommitIntended: true, BeyondGrace));
        _autoWizardJobRunner.IsRunning(jobId).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var blocked = (NextPeriodAutoCommitBlockedTriggerEvent)events[0];
        Assert.Multiple(() =>
        {
            Assert.That(blocked.Reason, Is.EqualTo(NextPeriodAutoCommitBlockReason.Interrupted));
            Assert.That(blocked.ScenarioId, Is.EqualTo(scenario.Id));
            Assert.That(blocked.DedupKey, Does.Contain(scenario.Id.ToString()),
                "The scenario id keeps a second draft for the same period from being folded into the first.");
        });
        await _autoWizardJobRunner.DidNotReceiveWithAnyArgs().StartAsync(default!, default);
        _autoCommitService.DidNotReceiveWithAnyArgs().QueueAutoCommit(default, default, default!, default, default);
    }

    [Test]
    public async Task DetectAsync_InterruptedAlreadyReported_SecondTickStaysSilent()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        var scenario = ActiveFebruaryScenario(group.Id);
        StubScenarios(scenario);
        var jobId = Guid.NewGuid();
        StubOpenConditions(
            AutofillLedgerRow(group.Id, jobId, autoCommitIntended: true, BeyondGrace),
            OutcomeLedgerRow(new NextPeriodAutoCommitBlockedTriggerEvent(
                group.Id, group.Name, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), scenario.Id, 0,
                NextPeriodAutoCommitBlockReason.Interrupted)));
        _autoWizardJobRunner.IsRunning(jobId).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WatcherAlreadyReportedATimeout_DoesNotAlsoReportInterrupted()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(ActiveFebruaryScenario(group.Id));
        var jobId = Guid.NewGuid();
        StubOpenConditions(
            AutofillLedgerRow(group.Id, jobId, autoCommitIntended: true, BeyondGrace),
            OutcomeLedgerRow(new NextPeriodAutoCommitBlockedTriggerEvent(
                group.Id, group.Name, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), null, 0,
                NextPeriodAutoCommitBlockReason.Timeout)));
        _autoWizardJobRunner.IsRunning(jobId).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty,
            "A finished watcher that blocked leaves the same draft behind; reporting it again as interrupted would double-notify.");
    }

    [Test]
    public async Task DetectAsync_AutofillRunWithoutAutoCommitIntent_IsNeverInterrupted()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(ActiveFebruaryScenario(group.Id));
        var jobId = Guid.NewGuid();
        StubOpenConditions(AutofillLedgerRow(group.Id, jobId, autoCommitIntended: false, BeyondGrace));
        _autoWizardJobRunner.IsRunning(jobId).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AutoCommitJobStillRunning_IsNotInterrupted()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(ActiveFebruaryScenario(group.Id));
        var jobId = Guid.NewGuid();
        StubOpenConditions(AutofillLedgerRow(group.Id, jobId, autoCommitIntended: true, BeyondGrace));
        _autoWizardJobRunner.IsRunning(jobId).Returns(true);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AutoCommitRunInsideGraceWindow_IsNotYetInterrupted()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        StubScenarios(ActiveFebruaryScenario(group.Id));
        var jobId = Guid.NewGuid();
        StubOpenConditions(AutofillLedgerRow(group.Id, jobId, autoCommitIntended: true, NowUtc.AddMinutes(-1)));
        _autoWizardJobRunner.IsRunning(jobId).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty,
            "The job registry is per API instance; inside the grace window another instance may still be watching this very chain.");
    }

    [Test]
    public async Task DetectAsync_AcceptedScenario_IsNeverReportedAsInterrupted()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        StubGroups(group);
        var accepted = ActiveFebruaryScenario(group.Id);
        accepted.Status = AnalyseScenarioStatus.Accepted;
        StubScenarios(accepted);
        var jobId = Guid.NewGuid();
        StubOpenConditions(AutofillLedgerRow(group.Id, jobId, autoCommitIntended: true, BeyondGrace));
        _autoWizardJobRunner.IsRunning(jobId).Returns(false);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
