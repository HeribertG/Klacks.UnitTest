// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that the period close judges a run of consecutive work days that crosses the period start in full, like the
/// live error list: the run is reported on the first day of the period with its whole length, a run that reaches past
/// the period end is counted in full, and a run that ended before the period is not reported at all. Findings other
/// than the work-time rules stay on the period's own rows.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Scheduling;
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
public class PeriodValidationLoaderBoundaryRunTests
{
    private const string ConsecutiveDaysCode = "ConsecutiveDays";
    private const string CollisionCode = "Collision";

    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly To = new(2026, 7, 31);

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
            MinRestHours: TimeSpan.FromHours(11),
            MaxDailyHours: TimeSpan.FromHours(24),
            MaxConsecutiveDays: 6,
            MaxWeeklyHours: TimeSpan.FromHours(168),
            MinRestDays: 0);
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
        var compensatoryRestEvaluator = Substitute.For<ICompensatoryRestEvaluator>();
        compensatoryRestEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<ScheduleValidationNotificationDto>());
        var holidayWorkEvaluator = Substitute.For<IHolidayWorkEvaluator>();
        holidayWorkEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<DateOnly>>(), Arg.Any<CancellationToken>())
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
            Substitute.For<ICompensatoryRestObligationReconciler>(),
            compensatoryRestEvaluator,
            holidayWorkEvaluator,
            planningRuleEvaluator);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task LoadAsync_RunCrossingThePeriodStart_IsCountedInFullOnTheFirstDay()
    {
        var clientId = SeedClient();
        SeedDailyWorks(clientId, From.AddDays(-5), From.AddDays(4));
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null);

        var run = issues.Where(i => i.Code == ConsecutiveDaysCode).ShouldHaveSingleItem();
        run.Date.ShouldBe(From);
        run.MessageParams["actualDays"].ShouldBe("10");
    }

    [Test]
    public async Task LoadAsync_RunEndingBeforeThePeriod_IsNotReported()
    {
        var clientId = SeedClient();
        SeedDailyWorks(clientId, From.AddDays(-12), From.AddDays(-2));
        SeedDailyWorks(clientId, From.AddDays(3), From.AddDays(3));
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null);

        issues.ShouldNotContain(i => i.Code == ConsecutiveDaysCode);
    }

    [Test]
    public async Task LoadAsync_RunCrossingThePeriodEnd_IsCountedInFullOnItsFirstDay()
    {
        var clientId = SeedClient();
        SeedDailyWorks(clientId, To.AddDays(-2), To.AddDays(5));
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null);

        var run = issues.Where(i => i.Code == ConsecutiveDaysCode).ShouldHaveSingleItem();
        run.Date.ShouldBe(To.AddDays(-2));
        run.MessageParams["actualDays"].ShouldBe("8");
    }

    [Test]
    public async Task LoadAsync_CollisionBeforeThePeriod_StaysOutOfTheIssues()
    {
        var clientId = SeedClient();
        var dayBefore = From.AddDays(-1);
        var dayInside = From.AddDays(2);
        SeedDailyWorks(clientId, dayBefore, dayBefore);
        SeedDailyWorks(clientId, dayBefore, dayBefore);
        SeedDailyWorks(clientId, dayInside, dayInside);
        SeedDailyWorks(clientId, dayInside, dayInside);
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null);

        var collisions = issues.Where(i => i.Code == CollisionCode).ToList();
        collisions.ShouldNotBeEmpty();
        collisions.ShouldAllBe(i => i.Date == dayInside);
    }

    private Guid SeedClient()
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client { Id = clientId, Name = "Anna", FirstName = "A" });
        return clientId;
    }

    private void SeedDailyWorks(Guid clientId, DateOnly first, DateOnly last)
    {
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            _context.Work.Add(new Work
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ShiftId = Guid.NewGuid(),
                CurrentDate = date,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                WorkTime = 8m
            });
        }
    }
}
