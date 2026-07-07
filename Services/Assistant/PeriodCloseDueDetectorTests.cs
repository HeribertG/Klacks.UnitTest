// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodCloseDueDetector — covers period-end computation per PaymentInterval
/// and the "already sealed" skip path.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseDueDetectorTests
{
    private IGroupRepository _groupRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private PeriodCloseDueDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        StubWeekStart(DayOfWeek.Monday);
        _sut = CreateSut(new DateOnly(2026, 1, 10));
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

    private PeriodCloseDueDetector CreateSut(DateOnly today)
    {
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new PeriodCloseDueDetector(_groupRepository, _sealedDayRepository, _weekConfiguration,
            NullLogger<PeriodCloseDueDetector>.Instance, tp);
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
        _groupRepository.List().Returns(new List<Group>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_IndividualInterval_AlwaysSkipped()
    {
        _groupRepository.List().Returns(new List<Group> { MakeGroup(PaymentInterval.Individual) });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
        await _sealedDayRepository.DidNotReceiveWithAnyArgs().GetRangeAsync(default, default, default, default);
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_NotInWindow_Skips()
    {
        // SetUp date is 2026-01-10, which is 21 days before month-end — outside the 3-day warn window
        _groupRepository.List().Returns(new List<Group> { MakeGroup(PaymentInterval.Monthly) });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_DefaultMondayWeekStart_PeriodEndsOnSunday()
    {
        var group = MakeGroup(PaymentInterval.Weekly);
        _groupRepository.List().Returns(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodCloseDueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 11)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(1));
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_ConfiguredSundayWeekStart_PeriodEndsOnSaturday()
    {
        var group = MakeGroup(PaymentInterval.Weekly);
        _groupRepository.List().Returns(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
        StubWeekStart(DayOfWeek.Sunday);
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodCloseDueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 10)));
        Assert.That(evt.DaysUntilDue, Is.EqualTo(0));
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_AlreadySealedAtEnd_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), group.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Id = Guid.NewGuid() } });
        _sut = CreateSut(new DateOnly(2026, 1, 28));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
