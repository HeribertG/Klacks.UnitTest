// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the pre-commit conflict checker and its six rule evaluators on their real implementations over one
/// in-memory database, and seeds one plan in which every rule family reports something: nine people with two
/// scheduling rules, collisions, a short rest, a full week, qualification gaps, a swap, a hand-over, period caps
/// (month, rolling average, rule-scoped quarter, overtime), counter rules, a rest-day rotation, restricted time
/// windows, compensatory-rest obligations, two holidays and a day directive. ShortRest started ten days before the
/// plan and worked the Sunday before it, so only the employment start keeps the rest-day rotation from reporting,
/// while Swapper worked the same Sunday and is reported. Collider and Lenient collide with the correction of their
/// own work and Outdoor with an absence, so they depend on the work changes and breaks of the right person. The
/// tests compare a check of all people in one call with the checks of each person alone. Only the holiday calendar,
/// the planning-rule evaluator, the directive keyword set and collaborators that no check reads are substitutes.
/// Next to the plan the fixture seeds rows that must not count (Disturbances), one per kind and each on a person of its
/// own, chosen so that counting it would add a finding of the stated comment to that person: a work with a work change
/// and a break of the other world (scenario token in the real plan, real plan in a scenario), a soft-deleted work,
/// break, work change and compensatory-rest obligation, and a container child (ParentWorkId set) of the person's own
/// work. The added rows lie where they overlap the planned row of their person only. The mode None leaves the rows out
/// and Live seeds the same rows as ordinary rows of the plan, which lets a test show that each one is effective.
/// </summary>
/// <param name="analyseToken">Token the seeded works, breaks and commands carry; null seeds the real plan</param>
/// <param name="disturbances">Whether the rows that must not count are left out, seeded as such or seeded as ordinary rows</param>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Holidays;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

public sealed class PreCommitBatchParityFixture : IDisposable
{
    public static readonly DateOnly Tuesday = new(2026, 3, 10);
    public static readonly DateOnly Monday = Tuesday.AddDays(-1);
    public static readonly DateOnly Wednesday = Tuesday.AddDays(1);
    public static readonly DateOnly Thursday = Tuesday.AddDays(2);
    public static readonly DateOnly SundayBefore = Tuesday.AddDays(-2);
    public static readonly DateOnly Sunday = Tuesday.AddDays(5);
    public static readonly DateOnly NextSunday = Tuesday.AddDays(12);

    public static readonly Guid Collider = Guid.NewGuid();
    public static readonly Guid ShortRest = Guid.NewGuid();
    public static readonly Guid Lenient = Guid.NewGuid();
    public static readonly Guid FullWeek = Guid.NewGuid();
    public static readonly Guid Unqualified = Guid.NewGuid();
    public static readonly Guid ExpiredQualification = Guid.NewGuid();
    public static readonly Guid Swapper = Guid.NewGuid();
    public static readonly Guid GiverOnly = Guid.NewGuid();
    public static readonly Guid Outdoor = Guid.NewGuid();

    private const string OutdoorGroupTag = "outdoor";
    private const decimal DisturbingWorkHours = 2m;
    private const string ImportHash = "irrelevant-for-this-test";

    private static readonly Guid StrictRuleId = Guid.NewGuid();
    private static readonly Guid LenientRuleId = Guid.NewGuid();
    public static readonly Guid HolidayCalendarId = Guid.NewGuid();
    private static readonly Guid QualifiedShiftId = Guid.NewGuid();
    private static readonly Guid OutdoorShiftId = Guid.NewGuid();
    private static readonly Guid PlainShiftId = Guid.NewGuid();
    private static readonly Guid SwappedWorkId = Guid.NewGuid();
    private static readonly Guid SeededQualificationId = Guid.NewGuid();
    private static readonly Guid ShortRestTuesdayWorkId = Guid.NewGuid();
    private static readonly Guid OtherScenarioToken = Guid.NewGuid();
    private static readonly DateTime ContractStart = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid? _analyseToken;
    private readonly PreCommitDisturbanceMode _disturbances;
    private Guid _vacationId;
    private Guid _lenientTuesdayWorkId;

    public PreCommitBatchParityFixture(
        Guid? analyseToken = null,
        PreCommitDisturbanceMode disturbances = PreCommitDisturbanceMode.Ignorable)
    {
        _analyseToken = analyseToken;
        _disturbances = disturbances;

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        Context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var settings = new SettingsRepository(
            Context,
            Substitute.For<ICalendarRuleFilterService>(),
            Substitute.For<ICalendarRuleSortingService>(),
            Substitute.For<ICalendarRulePaginationService>(),
            Substitute.For<IMacroManagementService>());
        var contracts = new ClientContractDataProvider(
            Context, NullLogger<ClientContractDataProvider>.Instance, new SettingsChangeVersion());
        var enforcement = new ComplianceEnforcementResolver(settings, NullLogger<ComplianceEnforcementResolver>.Instance);
        var membershipStarts = new ClientMembershipStartResolver(Context);
        var weekConfiguration = new WeekConfiguration(settings);
        var periodHours = new PeriodHoursService(
            Context,
            NullLogger<PeriodHoursService>.Instance,
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contracts,
            weekConfiguration,
            new FrozenPeriodPolicy(Context));

        PeriodCap = new PeriodCapEvaluator(
            new PeriodCapRuleRepository(Context),
            periodHours,
            enforcement,
            membershipStarts,
            contracts,
            new OvertimeConfigResolver(Context, contracts),
            new ClientWorkHoursProvider(Context),
            weekConfiguration);
        RestDayRotation = new RestDayRotationEvaluator(
            new RestDayRotationRuleRepository(Context), Context, enforcement, membershipStarts, contracts);
        CounterRule = new CounterRuleEvaluator(new CounterRuleRepository(Context), Context, enforcement, contracts);
        RestrictedTimeWindow = new RestrictedTimeWindowEvaluator(
            new RestrictedTimeWindowRuleRepository(Context), Context, enforcement);
        CompensatoryRest = new CompensatoryRestEvaluator(
            new CompensatoryRestObligationRepository(Context), enforcement, settings);
        HolidayCalendars = BuildHolidayCalendars();
        HolidayWork = new HolidayWorkEvaluator(
            new HolidayWorkExemptionRuleRepository(Context), HolidayCalendars, contracts, enforcement);

        var planningRules = Substitute.For<IPlanningRuleEvaluatorService>();
        planningRules
            .EvaluatePlannedChangeAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var keywords = Substitute.For<IScheduleCommandKeywordProvider>();
        keywords.GetAsync(Arg.Any<CancellationToken>()).Returns(ScheduleCommandKeywordTestFactory.Default);

        Checker = new PreCommitConflictChecker(
            Context,
            new ShiftRequiredQualificationRepository(Context, NullLogger<ShiftRequiredQualification>.Instance),
            new TimelineCalculationService(Options.Create(new ScheduleTimeOptions()), NullLogger<TimelineCalculationService>.Instance),
            new SchedulingPolicyResolver(contracts),
            new ComplianceEscalationService(enforcement),
            settings,
            PeriodCap,
            RestDayRotation,
            CounterRule,
            RestrictedTimeWindow,
            CompensatoryRest,
            HolidayWork,
            planningRules,
            new DayDirectiveConflictEvaluator(
                new ScheduleCommandRepository(Context, NullLogger<ScheduleCommand>.Instance), keywords));

        SeedPlan();
    }

    /// <summary>
    /// The rows that must not count, one per kind on a person of its own: the person, the comment its findings gain once
    /// the row counts, and whether a scenario check can see it (compensatory rest reports nothing in a scenario by design).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, Guid ClientId, string Comment, bool VisibleInScenario)> Disturbances =
    [
        ("work and work change of the other world", Unqualified, ScheduleValidationKeys.Collision, true),
        ("break of the other world", ExpiredQualification, ScheduleValidationKeys.Collision, true),
        ("soft-deleted work", Collider, ScheduleValidationKeys.Collision, true),
        ("soft-deleted break", FullWeek, ScheduleValidationKeys.Collision, true),
        ("soft-deleted work change", ShortRest, ScheduleValidationKeys.Collision, true),
        ("soft-deleted compensatory-rest obligation", Outdoor, ScheduleValidationKeys.CompensatoryRestDue, false),
        ("container child of the own work", Lenient, ScheduleValidationKeys.Collision, true),
    ];

    public DataBaseContext Context { get; }

    public PreCommitConflictChecker Checker { get; }

    public PeriodCapEvaluator PeriodCap { get; }

    public RestDayRotationEvaluator RestDayRotation { get; }

    public CounterRuleEvaluator CounterRule { get; }

    public RestrictedTimeWindowEvaluator RestrictedTimeWindow { get; }

    public CompensatoryRestEvaluator CompensatoryRest { get; }

    public HolidayWorkEvaluator HolidayWork { get; }

    public IClientHolidayCalendarResolver HolidayCalendars { get; }

    /// <summary>
    /// The planned rows of the plan, people interleaved: FullWeek has two rows on two different days, the second one
    /// last in the list.
    /// </summary>
    public IReadOnlyList<PlannedWorkRow> PlannedRows { get; } =
    [
        new(Collider, Tuesday, new TimeOnly(12, 0), new TimeOnly(20, 0), PlainShiftId),
        new(ShortRest, Tuesday, new TimeOnly(20, 0), new TimeOnly(23, 0), PlainShiftId),
        new(Lenient, Tuesday, new TimeOnly(20, 0), new TimeOnly(23, 0), PlainShiftId),
        new(FullWeek, Sunday, new TimeOnly(8, 0), new TimeOnly(16, 0), PlainShiftId, ExtraPaidHours: 1.5m),
        new(Unqualified, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0), QualifiedShiftId),
        new(ExpiredQualification, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0), QualifiedShiftId),
        new(Swapper, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0), PlainShiftId),
        new(Outdoor, Wednesday, new TimeOnly(23, 0), new TimeOnly(3, 0), OutdoorShiftId),
        new(FullWeek, NextSunday, new TimeOnly(22, 0), new TimeOnly(6, 0), PlainShiftId),
    ];

    /// <summary>The intervals the plan vacates: Swapper's own work by id, GiverOnly's work by its interval.</summary>
    public IReadOnlyList<PlannedRemovalRow> Removals { get; } =
    [
        new(Swapper, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0), SwappedWorkId),
        new(GiverOnly, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0), ExtraPaidHours: 2m),
    ];

    /// <summary>The people of the plan in the order the checker walks them: by first planned row, then by first removal.</summary>
    public IReadOnlyList<Guid> AffectedClients
        => PlannedRows.Select(row => row.ClientId).Concat(Removals.Select(removal => removal.ClientId)).Distinct().ToList();

    public static string Identity(ScheduleValidationNotificationDto finding)
        => $"{finding.Type}|{ConflictFingerprint.Of(finding)}";

    public void Dispose() => Context.Dispose();

    private static IClientHolidayCalendarResolver BuildHolidayCalendars()
    {
        var calculator = Substitute.For<IHolidaysListCalculator>();
        foreach (var holiday in new[] { Tuesday, Thursday })
        {
            calculator.IsHoliday(holiday).Returns(HolidayStatus.OfficialHoliday);
            calculator.GetHolidayInfo(holiday).Returns(new HolidayDate { Name = new MultiLanguage { En = "Holiday" } });
        }

        var calendars = Substitute.For<IClientHolidayCalendarResolver>();
        calendars.GetCalculatorAsync(Arg.Is<Guid?>(id => id == null), Arg.Any<int>())
            .Returns((IHolidaysListCalculator?)null);
        calendars.GetCalculatorAsync(Arg.Is<Guid?>(id => id == HolidayCalendarId), Arg.Any<int>())
            .Returns(calculator);
        return calendars;
    }

    private void SeedPlan()
    {
        SeedSettings();
        SeedSchedulingRulesAndPeople();
        SeedWorks();
        SeedQualifications();
        SeedComplianceRules();
        SeedObligationsAndDirectives();
        if (_disturbances != PreCommitDisturbanceMode.None)
        {
            SeedDisturbances();
        }

        Context.SaveChanges();
        Context.ChangeTracker.Clear();
    }

    private void SeedSettings()
    {
        AddSetting(SettingKeys.ComplianceEnforcementMinRestHours, ComplianceEnforcementModeValues.Block);
        AddSetting(SettingKeys.ComplianceCompensatoryRestEnabled, "true");
    }

    private void SeedSchedulingRulesAndPeople()
    {
        Context.SchedulingRules.Add(new SchedulingRule
        {
            Id = StrictRuleId,
            Name = "Strict",
            MinPauseHours = 11m,
            MaxDailyHours = 10m,
            MaxWeeklyHours = 50m,
            MaxConsecutiveDays = 6,
            MinRestDays = 1m,
            OvertimeThreshold = 40m,
        });
        Context.SchedulingRules.Add(new SchedulingRule
        {
            Id = LenientRuleId,
            Name = "Lenient",
            MinPauseHours = 0m,
            MaxDailyHours = 24m,
            MaxWeeklyHours = 168m,
            MaxConsecutiveDays = 31,
            MinRestDays = 0m,
            NightStart = "23:00",
            NightEnd = "06:00",
        });

        AddPerson(Collider, StrictRuleId);
        AddPerson(ShortRest, StrictRuleId, membershipStart: Tuesday.AddDays(-10));
        AddPerson(Lenient, LenientRuleId, calendarSelectionId: HolidayCalendarId);
        AddPerson(FullWeek, StrictRuleId, membershipStart: new DateOnly(2020, 1, 1));
        AddPerson(Unqualified, schedulingRuleId: null);
        AddPerson(ExpiredQualification, schedulingRuleId: null);
        AddPerson(Swapper, StrictRuleId);
        AddPerson(GiverOnly, StrictRuleId);
        AddPerson(Outdoor, LenientRuleId, calendarSelectionId: HolidayCalendarId);
    }

    private void SeedWorks()
    {
        AddWorkWithCorrectedEnd(Collider, new TimeOnly(8, 0), new TimeOnly(11, 0), correctedEnd: new TimeOnly(16, 0));
        AddWork(ShortRest, Tuesday, new TimeOnly(6, 0), new TimeOnly(14, 0), id: ShortRestTuesdayWorkId);
        AddWork(ShortRest, SundayBefore, new TimeOnly(8, 0), new TimeOnly(16, 0));
        _lenientTuesdayWorkId = AddWorkWithCorrectedEnd(
            Lenient, new TimeOnly(6, 0), new TimeOnly(14, 0), correctedEnd: new TimeOnly(21, 0));
        for (var day = Monday; day < Sunday; day = day.AddDays(1))
        {
            AddWork(FullWeek, day, new TimeOnly(6, 0), new TimeOnly(15, 0), hours: 9m);
        }

        AddWork(Swapper, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0), id: SwappedWorkId);
        AddWork(Swapper, SundayBefore, new TimeOnly(8, 0), new TimeOnly(16, 0));
        AddWork(GiverOnly, Tuesday, new TimeOnly(8, 0), new TimeOnly(16, 0));

        var vacation = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = "Ferien" },
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            IsOnCall = false,
        };
        Context.Absence.Add(vacation);
        _vacationId = vacation.Id;
        Context.Break.Add(NewBreak(Outdoor, Wednesday));
    }

    private void SeedQualifications()
    {
        Context.Qualification.Add(new Qualification { Id = SeededQualificationId, Name = new MultiLanguage { De = "Qualification" } });
        Context.ShiftRequiredQualification.Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = QualifiedShiftId,
            QualificationId = SeededQualificationId,
            IsMandatory = true,
            MinLevel = QualificationLevel.Basic,
        });
        Context.ClientQualification.Add(new ClientQualification
        {
            Id = Guid.NewGuid(),
            ClientId = ExpiredQualification,
            QualificationId = SeededQualificationId,
            Level = QualificationLevel.Basic,
            ValidUntil = Tuesday.AddDays(-10),
        });
    }

    private void SeedComplianceRules()
    {
        Context.PeriodCapRule.AddRange(
            CapRule(PeriodCapPeriod.Month, PeriodCapScope.TotalHours, capHours: 30m, warnAtPercent: 20),
            CapRule(PeriodCapPeriod.Month, PeriodCapScope.TotalHours, capHours: 0m, rollingWindowWeeks: 2, maxAverageWeeklyHours: 10m),
            CapRule(PeriodCapPeriod.Quarter, PeriodCapScope.TotalHours, capHours: 5m, schedulingRuleId: LenientRuleId),
            CapRule(PeriodCapPeriod.Month, PeriodCapScope.OvertimeHours, capHours: 1m));

        Context.CounterRule.AddRange(
            Counter(CounterEventType.NightShift, CounterPeriod.Week, threshold: 1),
            Counter(CounterEventType.WorkedDayInWeek, CounterPeriod.Week, threshold: 7, enforcement: RuleEnforcementMode.Block),
            Counter(CounterEventType.NightShift, CounterPeriod.Month, threshold: 1, schedulingRuleId: LenientRuleId));

        Context.RestDayRotationRule.Add(new RestDayRotationRule
        {
            Id = Guid.NewGuid(),
            DayOfWeek = DayOfWeek.Sunday,
            MinFreeCount = 4,
            WindowWeeks = 4,
            ImportSourceKey = "test:rest-day-rotation",
            ImportContentHash = ImportHash,
        });

        Context.RestrictedTimeWindowRule.AddRange(
            Window(new TimeOnly(22, 0), new TimeOnly(5, 0), groupTag: string.Empty),
            Window(new TimeOnly(0, 0), new TimeOnly(4, 0), OutdoorGroupTag));
        var outdoorGroupId = Guid.NewGuid();
        Context.Group.Add(new Group { Id = outdoorGroupId, Name = OutdoorGroupTag });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = outdoorGroupId, ShiftId = OutdoorShiftId });
    }

    private void SeedObligationsAndDirectives()
    {
        Context.CompensatoryRestObligation.AddRange(
            Obligation(ShortRest, Tuesday.AddDays(-2), dueDate: Tuesday.AddDays(3)),
            Obligation(FullWeek, Tuesday.AddDays(-3), dueDate: Tuesday.AddDays(8)),
            Obligation(FullWeek, Tuesday.AddDays(-4), dueDate: Tuesday.AddDays(20)));

        Context.Set<ScheduleCommand>().Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = Outdoor,
            CurrentDate = Wednesday,
            CommandKeyword = ScheduleCommandKeywordTestFactory.Default.FreeToken,
            AnalyseToken = _analyseToken,
        });
    }

    private void AddSetting(string type, string value)
        => Context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings { Id = Guid.NewGuid(), Type = type, Value = value });

    private void AddPerson(Guid clientId, Guid? schedulingRuleId, Guid? calendarSelectionId = null, DateOnly? membershipStart = null)
    {
        var client = new Client { Id = clientId, Name = clientId.ToString(), Type = EntityTypeEnum.Employee };
        if (membershipStart is { } start)
        {
            client.Membership = new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ValidFrom = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };
        }

        Context.Client.Add(client);
        if (schedulingRuleId is null)
        {
            return;
        }

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract " + clientId,
            PaymentInterval = PaymentInterval.Monthly,
            ValidFrom = ContractStart,
            GuaranteedHours = 160m,
            SchedulingRuleId = schedulingRuleId,
            CalendarSelectionId = calendarSelectionId,
        };
        Context.Contract.Add(contract);
        Context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ContractId = contract.Id,
            FromDate = DateOnly.FromDateTime(ContractStart),
            IsActive = true,
        });
    }

    private void SeedDisturbances()
    {
        var counts = _disturbances == PreCommitDisturbanceMode.Live;
        var otherWorld = counts ? _analyseToken : OtherWorldToken();

        var otherWorldWork = NewWork(Unqualified, Tuesday, new TimeOnly(6, 0), new TimeOnly(7, 0), hours: 1m);
        otherWorldWork.AnalyseToken = otherWorld;
        var otherWorldChange = NewCorrection(otherWorldWork.Id, new TimeOnly(7, 0), new TimeOnly(9, 0));
        otherWorldChange.AnalyseToken = otherWorld;
        var otherWorldBreak = NewBreak(ExpiredQualification, Tuesday);
        otherWorldBreak.AnalyseToken = otherWorld;

        var deletedWork = NewWork(Collider, Tuesday, new TimeOnly(17, 0), new TimeOnly(19, 0), DisturbingWorkHours);
        deletedWork.IsDeleted = !counts;
        var deletedBreak = NewBreak(FullWeek, Sunday);
        deletedBreak.IsDeleted = !counts;
        var deletedChange = NewCorrection(ShortRestTuesdayWorkId, new TimeOnly(14, 0), new TimeOnly(21, 0));
        deletedChange.IsDeleted = !counts;
        var deletedObligation = Obligation(Outdoor, Tuesday.AddDays(-5), dueDate: Tuesday.AddDays(10));
        deletedObligation.IsDeleted = !counts;

        var containerChild = NewWork(Lenient, Tuesday, new TimeOnly(21, 30), new TimeOnly(22, 30), hours: 1m);
        containerChild.ParentWorkId = counts ? null : _lenientTuesdayWorkId;

        Context.Work.AddRange(otherWorldWork, deletedWork, containerChild);
        Context.WorkChange.AddRange(otherWorldChange, deletedChange);
        Context.Break.AddRange(otherWorldBreak, deletedBreak);
        Context.CompensatoryRestObligation.Add(deletedObligation);
    }

    private Guid? OtherWorldToken() => _analyseToken is null ? OtherScenarioToken : null;

    private void AddWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end, decimal hours = 8m, Guid? id = null)
        => Context.Work.Add(NewWork(clientId, date, start, end, hours, id));

    private Work NewWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end, decimal hours = 8m, Guid? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = PlainShiftId,
            CurrentDate = date,
            StartTime = start,
            EndTime = end,
            WorkTime = hours,
            AnalyseToken = _analyseToken,
        };

    private Break NewBreak(Guid clientId, DateOnly date)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = _vacationId,
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(0, 0),
            WorkTime = 8m,
            AnalyseToken = _analyseToken,
        };

    private static WorkChange NewCorrection(Guid workId, TimeOnly start, TimeOnly end)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkId = workId,
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = (decimal)(end - start).TotalHours,
            StartTime = start,
            EndTime = end,
        };

    private Guid AddWorkWithCorrectedEnd(Guid clientId, TimeOnly start, TimeOnly end, TimeOnly correctedEnd)
    {
        var workId = Guid.NewGuid();
        var workHours = (decimal)(end - start).TotalHours;
        AddWork(clientId, Tuesday, start, end, workHours, workId);
        Context.WorkChange.Add(NewCorrection(workId, end, correctedEnd));
        return workId;
    }

    private static PeriodCapRule CapRule(
        PeriodCapPeriod period,
        PeriodCapScope scope,
        decimal capHours,
        int? warnAtPercent = null,
        int? rollingWindowWeeks = null,
        decimal? maxAverageWeeklyHours = null,
        Guid? schedulingRuleId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Period = period,
            Scope = scope,
            CapHours = capHours,
            WarnAtPercent = warnAtPercent,
            RollingWindowWeeks = rollingWindowWeeks,
            MaxAverageWeeklyHours = maxAverageWeeklyHours,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = "test:period-cap:" + Guid.NewGuid(),
            ImportContentHash = ImportHash,
        };

    private static CounterRule Counter(
        CounterEventType eventType,
        CounterPeriod period,
        int threshold,
        RuleEnforcementMode? enforcement = null,
        Guid? schedulingRuleId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Period = period,
            Threshold = threshold,
            Enforcement = enforcement,
            SchedulingRuleId = schedulingRuleId,
            ImportSourceKey = "test:counter:" + Guid.NewGuid(),
            ImportContentHash = ImportHash,
        };

    private static RestrictedTimeWindowRule Window(TimeOnly dailyStart, TimeOnly dailyEnd, string groupTag)
        => new()
        {
            Id = Guid.NewGuid(),
            SeasonFromMonth = 1,
            SeasonFromDay = 1,
            SeasonToMonth = 12,
            SeasonToDay = 31,
            DailyStart = dailyStart,
            DailyEnd = dailyEnd,
            AppliesToGroupTag = groupTag,
            ImportSourceKey = "test:restricted-window:" + Guid.NewGuid(),
            ImportContentHash = ImportHash,
        };

    private static CompensatoryRestObligation Obligation(Guid clientId, DateOnly triggerDate, DateOnly dueDate)
        => new()
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TriggerDate = triggerDate,
            RestGapStart = triggerDate.ToDateTime(new TimeOnly(22, 0), DateTimeKind.Utc),
            ShortfallHours = 3m,
            StandardRestHours = 11m,
            DueDate = dueDate,
        };
}
