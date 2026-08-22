// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PreCommitConflictChecker using an in-memory DataBaseContext, the real
/// TimelineCalculationService and a stubbed scheduling policy. Verifies that a planned placement's
/// NEW conflicts are detected (collision = blocking, rest = warning), that a clean placement returns
/// nothing, that a pre-existing violation is NOT attributed to the placement (before/after diff),
/// and that planned work on a statutory holiday blocks in Block mode unless an exemption covers it.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.CalendarSelections;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Holidays;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class PreCommitConflictCheckerTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid ClientA = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private PreCommitConflictChecker _checker = null!;
    private IComplianceEnforcementResolver _enforcementResolver = null!;
    private ISettingsReader _settingsReader = null!;
    private ICompensatoryRestEvaluator _compensatoryRestEvaluator = null!;
    private IPeriodCapEvaluator _periodCapEvaluator = null!;
    private IRestDayRotationEvaluator _restDayRotationEvaluator = null!;
    private ICounterRuleEvaluator _counterRuleEvaluator = null!;
    private IRestrictedTimeWindowEvaluator _restrictedTimeWindowEvaluator = null!;
    private IHolidayWorkEvaluator _holidayWorkEvaluator = null!;
    private TimelineCalculationService _timelineCalculator = null!;
    private ISchedulingPolicyResolver _policyResolver = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var timeOptions = Options.Create(new ScheduleTimeOptions());
        _timelineCalculator = new TimelineCalculationService(
            timeOptions, Substitute.For<ILogger<TimelineCalculationService>>());

        _policyResolver = Substitute.For<ISchedulingPolicyResolver>();
        _policyResolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(new SchedulingPolicy(
                MinRestHours: TimeSpan.FromHours(11),
                MaxDailyHours: TimeSpan.FromHours(10),
                MaxConsecutiveDays: 6,
                MaxWeeklyHours: TimeSpan.FromHours(50),
                MinRestDays: 2));

        _enforcementResolver = Substitute.For<IComplianceEnforcementResolver>();
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Warn);

        _settingsReader = Substitute.For<ISettingsReader>();

        _periodCapEvaluator = Substitute.For<IPeriodCapEvaluator>();
        _periodCapEvaluator
            .EvaluatePlannedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        _restDayRotationEvaluator = Substitute.For<IRestDayRotationEvaluator>();
        _restDayRotationEvaluator
            .EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        _counterRuleEvaluator = Substitute.For<ICounterRuleEvaluator>();
        _counterRuleEvaluator
            .EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        _restrictedTimeWindowEvaluator = Substitute.For<IRestrictedTimeWindowEvaluator>();
        _restrictedTimeWindowEvaluator
            .EvaluatePlannedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime, Guid? ShiftId)>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        _compensatoryRestEvaluator = NonReportingCompensatoryRestEvaluator();
        _holidayWorkEvaluator = NonReportingHolidayWorkEvaluator();

        _checker = BuildChecker(_holidayWorkEvaluator);
    }

    private PreCommitConflictChecker BuildChecker(IHolidayWorkEvaluator holidayWorkEvaluator)
        => new(_context, _timelineCalculator, _policyResolver, new ComplianceEscalationService(_enforcementResolver), _settingsReader, _periodCapEvaluator, _restDayRotationEvaluator, _counterRuleEvaluator, _restrictedTimeWindowEvaluator,
            _compensatoryRestEvaluator, holidayWorkEvaluator);

    [TearDown]
    public void TearDown() => _context.Dispose();

    private void SeedWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end)
    {
        _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = 8,
            ParentWorkId = null,
            AnalyseToken = null
        });
        _context.SaveChanges();
    }

    private static PlannedWorkRow Row(TimeOnly start, TimeOnly end, DateOnly? date = null)
        => new(ClientA, date ?? Day, start, end);

    [Test]
    public async Task Collision_IsDetectedAsBlocking()
    {
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(12, 0), new TimeOnly(20, 0))]);

        result.HasAny.ShouldBeTrue();
        result.HasBlocking.ShouldBeTrue();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.collision" && c.Type == ScheduleValidationType.Error);
    }

    [Test]
    public async Task RestViolation_IsWarningNotBlocking()
    {
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(20, 0), new TimeOnly(23, 0))]);

        result.HasAny.ShouldBeTrue();
        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.rest-violation" && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task CleanPlacement_ReturnsNoConflicts()
    {
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(5))]);

        result.HasAny.ShouldBeFalse();
        result.NewConflicts.ShouldBeEmpty();
    }

    [Test]
    public async Task PreExistingViolation_IsNotAttributedToPlacement()
    {
        // Two already-colliding works on Day → the collision exists in the baseline.
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedWork(ClientA, Day, new TimeOnly(12, 0), new TimeOnly(20, 0));

        // Place an unrelated, conflict-free work far away.
        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(10))]);

        result.HasAny.ShouldBeFalse();
        result.NewConflicts.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyInput_ReturnsEmpty()
    {
        var result = await _checker.CheckAsync(Array.Empty<PlannedWorkRow>());

        result.HasAny.ShouldBeFalse();
    }

    [Test]
    public async Task AggregateWorsening_ReportedAsNew_ButStillNonBlocking()
    {
        // Documents the known diff behaviour for aggregate checks: the week (Mon 02 - Sat 07) is
        // already over MaxWeeklyHours (6 x 9h = 54h > 50h) in the baseline. Placing a 7th same-week
        // work (Sunday 08) pushes the bucket to 62h; because the key includes actualHours, the
        // worsened weekly-overtime is reported as NEW. It stays a Warning, so it never blocks.
        var monday = new DateOnly(2026, 3, 2);
        for (var i = 0; i < 6; i++)
        {
            SeedWork(ClientA, monday.AddDays(i), new TimeOnly(6, 0), new TimeOnly(15, 0));
        }

        var sunday = monday.AddDays(6);
        var result = await _checker.CheckAsync(
            [new PlannedWorkRow(ClientA, sunday, new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.weekly-overtime" && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task ScenarioRows_DoNotSeeRealWorks()
    {
        // A real work exists on Day; checking a scenario placement (analyseToken set) must not collide
        // with the real work, because scenario mode only loads rows with that token.
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _checker.CheckAsync(
            [Row(new TimeOnly(12, 0), new TimeOnly(20, 0))], analyseToken: Guid.NewGuid());

        result.HasAny.ShouldBeFalse();
    }

    [Test]
    public async Task BlockMode_EscalatesRestViolation_ToOverridableError()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.MinRestHours).Returns(RuleEnforcementMode.Block);
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(20, 0), new TimeOnly(23, 0))]);

        result.HasBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeFalse();
        result.HasOverridableBlocking.ShouldBeTrue();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.rest-violation"
            && c.Type == ScheduleValidationType.Error
            && c.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey] == ComplianceRuleNames.MinRestHours);
    }

    [Test]
    public async Task WarnMode_LeavesRestViolation_AsNonOverridableWarning()
    {
        SeedWork(ClientA, Day, new TimeOnly(6, 0), new TimeOnly(14, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(20, 0), new TimeOnly(23, 0))]);

        result.HasBlocking.ShouldBeFalse();
        result.HasOverridableBlocking.ShouldBeFalse();
    }

    [Test]
    public async Task Collision_IsNotHardBlocking_EvenWhenBlockModeConfigured()
    {
        // Owner decision 2026-08-22: a schedule collision is Error-level (still HasBlocking) but no
        // longer hard-blocking - it is reported into the error list, not refused at write time. Block
        // mode is configured for every rule name here on purpose (this overlap also trips an unrelated
        // Warning, e.g. overtime, that Block mode escalates - collateral, not the point of this test),
        // to prove the collision entry itself carries no EnforcementRuleParamKey and is therefore never
        // hard-blocking, regardless of enforcement mode.
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Block);
        SeedWork(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var result = await _checker.CheckAsync([Row(new TimeOnly(12, 0), new TimeOnly(20, 0))]);

        result.HasBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == "schedule.error-list.collision"
            && c.Type == ScheduleValidationType.Error
            && !c.CommentParams.ContainsKey(ComplianceRuleNames.EnforcementRuleParamKey));
    }

    [Test]
    public async Task ExpiringSoonMandatoryQualification_AddsWarning_NeverBlocks()
    {
        var qualificationId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        _settingsReader.GetSetting(SettingKeys.QualificationExpiryWarningDays)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.QualificationExpiryWarningDays, Value = "30" });

        _context.ShiftRequiredQualification.Add(new Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            QualificationId = qualificationId,
            IsMandatory = true,
            MinLevel = QualificationLevel.Basic
        });
        _context.ClientQualification.Add(new Klacks.Api.Domain.Models.Associations.ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = ClientA,
            QualificationId = qualificationId,
            Level = QualificationLevel.Basic,
            ValidUntil = Day.AddDays(10)
        });
        _context.SaveChanges();

        var row = new PlannedWorkRow(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), shiftId);
        var result = await _checker.CheckAsync([row]);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == QualificationValidationKeys.ExpiringSoon && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task MissingMandatoryQualification_IsHardBlocking_EvenWhenBlockModeConfigured()
    {
        // The one structural Error left that is never overridable and never silently accepted (owner
        // decision 2026-08-22 downgraded the schedule collision, this one is unaffected). Block mode is
        // configured here to prove eligibility hard-blocking does not depend on any enforcement mode -
        // unlike a collision, it was never gated on one.
        _enforcementResolver.GetModeAsync(Arg.Any<string>()).Returns(RuleEnforcementMode.Block);
        var shiftId = Guid.NewGuid();
        _context.ShiftRequiredQualification.Add(new Klacks.Api.Domain.Models.Associations.ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            QualificationId = Guid.NewGuid(),
            IsMandatory = true,
            MinLevel = QualificationLevel.Basic
        });
        _context.SaveChanges();

        var row = new PlannedWorkRow(ClientA, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), shiftId);
        var result = await _checker.CheckAsync([row]);

        result.HasBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeTrue();
        result.HasOverridableBlocking.ShouldBeFalse();
    }

    [Test]
    public async Task PeriodCapBreach_FromEvaluator_IsSurfacedAsWarning()
    {
        var periodCapEntry = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Warning,
            ClientId = ClientA,
            Date = Day,
            Comment = ScheduleValidationKeys.PeriodCap,
            CommentParams = new Dictionary<string, string> { ["actualHours"] = "210.0", ["capHours"] = "200" }
        };
        _periodCapEvaluator
            .EvaluatePlannedAsync(
                ClientA,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([periodCapEntry]);

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(20))]);

        result.HasBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c => c.Comment == ScheduleValidationKeys.PeriodCap && c.Type == ScheduleValidationType.Warning);
    }

    [Test]
    public async Task RestDayRotationError_FromEvaluator_PassesThroughWithoutDoubleEscalation()
    {
        // The evaluator escalates Block-mode violations itself; the checker's escalation pass must
        // surface the entry untouched (it skips anything that is already an Error).
        var rotationEntry = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = ClientA,
            Date = Day,
            Comment = ScheduleValidationKeys.RestDayRotation,
            CommentParams = new Dictionary<string, string>
            {
                ["dayOfWeek"] = "Sunday",
                ["actualFree"] = "0",
                ["minFree"] = "2",
                ["windowWeeks"] = "4",
                [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.RestDayRotation,
            }
        };
        _restDayRotationEvaluator
            .EvaluatePlannedAsync(
                ClientA,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, TimeOnly StartTime, TimeOnly EndTime)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([rotationEntry]);

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(20))]);

        result.HasBlocking.ShouldBeTrue();
        var surfaced = result.NewConflicts.Single(c => c.Comment == ScheduleValidationKeys.RestDayRotation);
        surfaced.Type.ShouldBe(ScheduleValidationType.Error);
        surfaced.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey].ShouldBe(ComplianceRuleNames.RestDayRotation);
    }

    [Test]
    public async Task PeriodCapBreach_BlockEnforcementFromEvaluator_IsOverridableBlocking()
    {
        var periodCapEntry = new ScheduleValidationNotificationDto
        {
            Type = ScheduleValidationType.Error,
            ClientId = ClientA,
            Date = Day,
            Comment = ScheduleValidationKeys.PeriodCap,
            CommentParams = new Dictionary<string, string>
            {
                ["actualHours"] = "210.0",
                ["capHours"] = "200",
                [ComplianceRuleNames.EnforcementRuleParamKey] = ComplianceRuleNames.PeriodCap
            }
        };
        _periodCapEvaluator
            .EvaluatePlannedAsync(
                ClientA,
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<(DateOnly Date, decimal Hours)>>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([periodCapEntry]);

        var result = await _checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0), Day.AddDays(20))]);

        result.HasBlocking.ShouldBeTrue();
        result.HasOverridableBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeFalse();
    }

    /// <summary>
    /// K12 reports persisted obligations, which these tests never seed - an evaluator reporting
    /// nothing keeps them about the rule they were written for. CompensatoryRestEvaluatorTests and
    /// the pre-commit K12 test cover the wiring itself.
    /// </summary>

    /// <summary>
    /// K12 is state, not a property of the planned rows: an obligation that already exists must still
    /// stop more work being added while it is outstanding. Before this the pre-commit gate had no
    /// compensatory-rest evaluator at all, so a placement passed while a rest was owed.
    /// </summary>
    [Test]
    public async Task CheckAsync_OpenCompensatoryRestObligation_IsReportedByTheGate()
    {
        var clientId = Guid.NewGuid();
        _compensatoryRestEvaluator
            .EvaluateAsync(clientId, Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>
            {
                new()
                {
                    Type = ScheduleValidationType.Warning,
                    ClientId = clientId,
                    ClientName = string.Empty,
                    Date = new DateOnly(2091, 3, 10),
                    Comment = ScheduleValidationKeys.CompensatoryRestOverdue,
                    CommentParams = new Dictionary<string, string>(),
                },
            });

        var result = await _checker.CheckAsync(
            [new PlannedWorkRow(clientId, new DateOnly(2091, 3, 12), new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        result.NewConflicts.ShouldContain(c => c.Comment == ScheduleValidationKeys.CompensatoryRestOverdue);
    }

    /// <summary>
    /// The obligation is anchored on the last day the plan touches, so a deadline inside the planned
    /// range is judged as passed rather than still pending.
    /// </summary>
    [Test]
    public async Task CheckAsync_SeveralPlannedDays_AsksTheEvaluatorForTheLastOne()
    {
        var clientId = Guid.NewGuid();

        await _checker.CheckAsync([
            new PlannedWorkRow(clientId, new DateOnly(2091, 3, 10), new TimeOnly(8, 0), new TimeOnly(16, 0)),
            new PlannedWorkRow(clientId, new DateOnly(2091, 3, 14), new TimeOnly(8, 0), new TimeOnly(16, 0)),
        ]);

        await _compensatoryRestEvaluator.Received(1).EvaluateAsync(
            clientId, Arg.Any<string>(), new DateOnly(2091, 3, 14), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Planned work on a statutory holiday with the holidayWork rule in Block mode must block the
    /// placement as an overridable (never hard) error. Before this the gate had no holiday-work
    /// evaluator at all, so the autofill could place holiday work unimpeded.
    /// </summary>
    [Test]
    public async Task HolidayWork_BlockMode_BlocksPlannedWorkOnStatutoryHoliday()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.HolidayWork).Returns(RuleEnforcementMode.Block);
        var checker = BuildChecker(RealHolidayWorkEvaluator());

        var result = await checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        result.HasBlocking.ShouldBeTrue();
        result.HasOverridableBlocking.ShouldBeTrue();
        result.HasHardBlocking.ShouldBeFalse();
        result.NewConflicts.ShouldContain(c =>
            c.Comment == ScheduleValidationKeys.HolidayWork
            && c.Type == ScheduleValidationType.Error
            && c.CommentParams[ComplianceRuleNames.EnforcementRuleParamKey] == ComplianceRuleNames.HolidayWork);
    }

    [Test]
    public async Task HolidayWork_ActiveGlobalExemption_LetsThePlacementPass()
    {
        _enforcementResolver.GetModeAsync(ComplianceRuleNames.HolidayWork).Returns(RuleEnforcementMode.Block);
        var checker = BuildChecker(RealHolidayWorkEvaluator(new HolidayWorkExemptionRule { SchedulingRuleId = null }));

        var result = await checker.CheckAsync([Row(new TimeOnly(8, 0), new TimeOnly(16, 0))]);

        result.HasAny.ShouldBeFalse();
        result.NewConflicts.ShouldBeEmpty();
    }

    /// <summary>
    /// The real evaluator on stubbed dependencies: Day is an official holiday for ClientA, whose
    /// contract references no scheduling rule, so only a global exemption can cover it.
    /// </summary>
    private HolidayWorkEvaluator RealHolidayWorkEvaluator(params HolidayWorkExemptionRule[] exemptions)
    {
        var exemptionRepository = Substitute.For<IHolidayWorkExemptionRuleRepository>();
        exemptionRepository.GetAllActiveAsync().Returns(exemptions.ToList());

        var calculator = Substitute.For<IHolidaysListCalculator>();
        calculator.IsHoliday(Day).Returns(HolidayStatus.OfficialHoliday);
        calculator.GetHolidayInfo(Day).Returns(new HolidayDate { CurrentName = "Holiday" });

        var calendarResolver = Substitute.For<IClientHolidayCalendarResolver>();
        calendarResolver.GetCalculatorAsync(Arg.Any<Guid?>(), Arg.Any<int>()).Returns(calculator);

        var contractData = Substitute.For<IClientContractDataProvider>();
        contractData
            .GetEffectiveContractDataForClientsRangeAsync(
                Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>
            {
                [Day] = new() { [ClientA] = new EffectiveContractData { SchedulingRuleId = null } },
            });

        return new HolidayWorkEvaluator(exemptionRepository, calendarResolver, contractData, _enforcementResolver);
    }

    private static IHolidayWorkEvaluator NonReportingHolidayWorkEvaluator()
    {
        var evaluator = Substitute.For<IHolidayWorkEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        return evaluator;
    }

    private static ICompensatoryRestEvaluator NonReportingCompensatoryRestEvaluator()
    {
        var evaluator = Substitute.For<ICompensatoryRestEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        return evaluator;
    }
}
