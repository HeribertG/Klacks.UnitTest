// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The rule check of a scenario (accept gate, compliance report after a run) reads the scenario's works. A group scenario
/// carries only the group's shifts; Anna's real Night duty in another group stays in the plan and keeps its place after
/// an accept. A scenario work that overlaps it must therefore surface as a collision, while a work of another scenario
/// never belongs to this one.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.PeriodClosing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.PeriodClosing;

[TestFixture]
public class PeriodValidationLoaderScenarioOutsideWorkTests
{
    private const string CollisionCode = "Collision";
    private const string UncoveredAbsenceCode = "UncoveredAbsence";
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly To = new(2026, 7, 31);
    private static readonly DateOnly Day = new(2026, 7, 14);
    private static readonly DateOnly InsideSpanDay = new(2026, 7, 8);
    private static readonly DateOnly BeyondSpanDay = new(2026, 7, 28);

    private DataBaseContext _context = null!;
    private PeriodValidationLoader _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);

        var policy = new SchedulingPolicy(
            TimeSpan.FromHours(11), TimeSpan.FromHours(24), 999, TimeSpan.FromHours(168), 0);
        var policyResolver = Substitute.For<ISchedulingPolicyResolver>();
        policyResolver.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>()).Returns(policy);
        policyResolver.GetForClientsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<DateOnly>())
            .Returns(new Dictionary<Guid, SchedulingPolicy>());

        var timelineService = new TimelineCalculationService(
            Options.Create(new ScheduleTimeOptions()), NullLogger<TimelineCalculationService>.Instance);

        var periodCapEvaluator = Substitute.For<IPeriodCapEvaluator>();
        periodCapEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restDayRotationEvaluator = Substitute.For<IRestDayRotationEvaluator>();
        restDayRotationEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var counterRuleEvaluator = Substitute.For<ICounterRuleEvaluator>();
        counterRuleEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var restrictedTimeWindowEvaluator = Substitute.For<IRestrictedTimeWindowEvaluator>();
        restrictedTimeWindowEvaluator.EvaluateRangeAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var reconciler = Substitute.For<ICompensatoryRestObligationReconciler>();
        var compensatoryRestEvaluator = Substitute.For<ICompensatoryRestEvaluator>();
        compensatoryRestEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var holidayWorkEvaluator = Substitute.For<IHolidayWorkEvaluator>();
        holidayWorkEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        var planningRuleEvaluator = Substitute.For<IPlanningRuleEvaluatorService>();
        planningRuleEvaluator.EvaluateRangeAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid?>(), Arg.Any<IReadOnlyDictionary<Guid, string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());

        _sut = new PeriodValidationLoader(
            _context,
            timelineService,
            policyResolver,
            periodCapEvaluator,
            restDayRotationEvaluator,
            counterRuleEvaluator,
            restrictedTimeWindowEvaluator,
            reconciler,
            compensatoryRestEvaluator,
            holidayWorkEvaluator,
            planningRuleEvaluator);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(true)]
    [TestCase(false)]
    public async Task LoadAsync_Scenario_WorkOverlappingARealDutyOutsideTheScenario_IsACollision(bool groupRun)
    {
        var token = Guid.NewGuid();
        var anna = SeedClient();
        Guid? groupId = groupRun ? Guid.NewGuid() : null;
        if (groupId is { } group)
        {
            _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = group, ClientId = anna });
        }
        var realCare = SeedShift(analyseToken: null, sourceShiftId: null);
        var scenarioCare = SeedShift(token, realCare);
        var realNight = SeedShift(analyseToken: null, sourceShiftId: null);
        SeedWork(anna, realNight, analyseToken: null, new TimeOnly(14, 0), new TimeOnly(22, 0));
        SeedWork(anna, scenarioCare, token, new TimeOnly(16, 0), new TimeOnly(20, 0));
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, groupId, token);

        issues.ShouldContain(i => i.Code == CollisionCode && i.ClientId == anna,
            "after an accept Anna would hold both duties at the same time");
    }

    [Test]
    public async Task LoadAsync_Scenario_WorkOfAnotherScenario_IsNoCollision()
    {
        var token = Guid.NewGuid();
        var otherToken = Guid.NewGuid();
        var anna = SeedClient();
        var realCare = SeedShift(analyseToken: null, sourceShiftId: null);
        var scenarioCare = SeedShift(token, realCare);
        var otherScenarioNight = SeedShift(otherToken, SeedShift(analyseToken: null, sourceShiftId: null));
        SeedWork(anna, otherScenarioNight, otherToken, new TimeOnly(14, 0), new TimeOnly(22, 0));
        SeedWork(anna, scenarioCare, token, new TimeOnly(16, 0), new TimeOnly(20, 0));
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null, token);

        issues.ShouldNotContain(i => i.Code == CollisionCode, "a work of another scenario is not part of this one");
    }

    [Test]
    public async Task LoadAsync_Scenario_WorkOverlappingARealDutyOnACarriedShiftBeyondTheClonedSpan_IsACollision()
    {
        var token = SeedScenario();
        var anna = SeedClient();
        var realCare = SeedShift(analyseToken: null, sourceShiftId: null);
        var scenarioCare = SeedShift(token, realCare);
        SeedWork(anna, realCare, analyseToken: null, new TimeOnly(14, 0), new TimeOnly(22, 0), BeyondSpanDay);
        SeedWork(anna, scenarioCare, token, new TimeOnly(16, 0), new TimeOnly(20, 0), BeyondSpanDay);
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null, token);

        issues.ShouldContain(i => i.Code == CollisionCode && i.ClientId == anna,
            "no clone exists beyond the cloned span, the real duty stays in the plan");
    }

    [Test]
    public async Task LoadAsync_Scenario_RealCopyOfAWorkInsideTheScenario_IsNoCollision()
    {
        var token = SeedScenario();
        var anna = SeedClient();
        var realCare = SeedShift(analyseToken: null, sourceShiftId: null);
        var scenarioCare = SeedShift(token, realCare);
        SeedWork(anna, realCare, analyseToken: null, new TimeOnly(14, 0), new TimeOnly(22, 0), InsideSpanDay);
        SeedWork(anna, scenarioCare, token, new TimeOnly(16, 0), new TimeOnly(20, 0), InsideSpanDay);
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null, token);

        issues.ShouldNotContain(i => i.Code == CollisionCode, "the clone stands for the real duty inside the cloned span");
    }

    [Test]
    public async Task LoadAsync_Scenario_WorkDuringARealAbsenceTheScenarioDoesNotCarry_IsUncovered()
    {
        var token = Guid.NewGuid();
        var anna = SeedClient();
        var scenarioCare = SeedShift(token, SeedShift(analyseToken: null, sourceShiftId: null));
        SeedWork(anna, scenarioCare, token, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedBreak(anna, Guid.NewGuid(), analyseToken: null);
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null, token);

        issues.ShouldContain(i => i.Code == UncoveredAbsenceCode && i.ClientId == anna,
            "the real absence stays in the plan after an accept");
    }

    [Test]
    public async Task LoadAsync_Scenario_AnAbsenceOfAnotherScenario_IsNoAbsenceOfThisOne()
    {
        var token = Guid.NewGuid();
        var anna = SeedClient();
        var scenarioCare = SeedShift(token, SeedShift(analyseToken: null, sourceShiftId: null));
        SeedWork(anna, scenarioCare, token, new TimeOnly(8, 0), new TimeOnly(16, 0));
        SeedBreak(anna, Guid.NewGuid(), Guid.NewGuid());
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null, token);

        issues.ShouldNotContain(i => i.Code == UncoveredAbsenceCode);
    }

    private Guid SeedScenario()
    {
        var token = Guid.NewGuid();
        _context.AnalyseScenarios.Add(new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Name = "Scenario",
            Token = token,
            FromDate = new DateOnly(2026, 7, 6),
            UntilDate = new DateOnly(2026, 7, 12),
        });
        return token;
    }

    private Guid SeedClient()
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client { Id = clientId, Name = "Anna", FirstName = "A" });
        return clientId;
    }

    private Guid SeedShift(Guid? analyseToken, Guid? sourceShiftId)
    {
        var id = Guid.NewGuid();
        _context.Shift.Add(new Shift
        {
            Id = id,
            Name = "Shift",
            Status = ShiftStatus.SplitShift,
            AnalyseToken = analyseToken,
            ScenarioSourceShiftId = sourceShiftId,
        });
        return id;
    }

    private void SeedWork(Guid clientId, Guid shiftId, Guid? analyseToken, TimeOnly start, TimeOnly end, DateOnly? date = null)
        => _context.Work.Add(new Work
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ShiftId = shiftId,
            CurrentDate = date ?? Day,
            StartTime = start,
            EndTime = end,
            WorkTime = 8m,
            AnalyseToken = analyseToken,
        });

    private void SeedBreak(Guid clientId, Guid absenceId, Guid? analyseToken)
        => _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = Day,
            StartTime = new TimeOnly(6, 0),
            EndTime = new TimeOnly(22, 0),
            WorkTime = 8m,
            AnalyseToken = analyseToken,
        });
}
