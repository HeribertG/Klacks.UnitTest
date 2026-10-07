// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests which dates the period close hands to the holiday-work detector: every calendar day a work of the period
/// actually covers, including the after-midnight part of a night shift - also when that part falls on the first day
/// after the period, because the work (and its accounting) belongs to the period being closed. Breaks never count.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
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
public class PeriodValidationLoaderHolidayWorkDatesTests
{
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly To = new(2026, 7, 31);

    private DataBaseContext _context = null!;
    private IHolidayWorkEvaluator _holidayWorkEvaluator = null!;
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

        _holidayWorkEvaluator = Substitute.For<IHolidayWorkEvaluator>();
        _holidayWorkEvaluator.EvaluateAsync(Arg.Any<Guid>(), Arg.Any<string>(),
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
            _holidayWorkEvaluator,
            planningRuleEvaluator);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task LoadAsync_NightShiftOnLastDay_HandsTheFollowingHolidayDateToTheDetector()
    {
        var clientId = SeedClient();
        SeedWork(clientId, To, new TimeOnly(22, 0), new TimeOnly(6, 0));
        await _context.SaveChangesAsync();

        await _sut.LoadAsync(From, To, null);

        await _holidayWorkEvaluator.Received(1).EvaluateAsync(
            clientId,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyCollection<DateOnly>>(dates => dates.SequenceEqual(new[] { To, To.AddDays(1) })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadAsync_NightShiftInsidePeriod_HandsBothCalendarDays()
    {
        var clientId = SeedClient();
        var day = new DateOnly(2026, 7, 14);
        SeedWork(clientId, day, new TimeOnly(22, 0), new TimeOnly(6, 0));
        await _context.SaveChangesAsync();

        await _sut.LoadAsync(From, To, null);

        await _holidayWorkEvaluator.Received(1).EvaluateAsync(
            clientId,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyCollection<DateOnly>>(dates => dates.SequenceEqual(new[] { day, day.AddDays(1) })),
            Arg.Any<CancellationToken>());
    }

    [TestCase(true, "OnCallOverlap", ScheduleValidationType.Warning)]
    [TestCase(false, "Collision", ScheduleValidationType.Error)]
    public async Task LoadAsync_WorkOverFullDayBreak_IsOnCallOverlapOnlyForOnCallTypes(
        bool isOnCall, string expectedCode, ScheduleValidationType expectedSeverity)
    {
        var clientId = SeedClient();
        var day = new DateOnly(2026, 7, 14);
        SeedWork(clientId, day, new TimeOnly(10, 0), new TimeOnly(14, 0));
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = "Typ" },
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            IsOnCall = isOnCall
        };
        _context.Absence.Add(absence);
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absence.Id,
            CurrentDate = day,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(0, 0),
            WorkTime = 8m
        });
        await _context.SaveChangesAsync();

        var issues = await _sut.LoadAsync(From, To, null);

        var issue = issues.Single(i => i.Code is "OnCallOverlap" or "Collision");
        issue.Code.ShouldBe(expectedCode);
        issue.Severity.ShouldBe(expectedSeverity);
    }

    private Guid SeedClient()
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client { Id = clientId, Name = "Anna", FirstName = "A" });
        return clientId;
    }

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
            WorkTime = 8m
        });
    }
}
