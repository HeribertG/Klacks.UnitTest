// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodOverdueDetector — covers the empty roster, the Individual skip,
/// the overdue threshold, severity buckets, the already-sealed skip, the weekly and
/// biweekly period-end computation and the group-younger-than-period guard.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodOverdueDetectorTests
{
    private IGroupRepository _groupRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private IWeekConfiguration _weekConfiguration = null!;
    private PeriodOverdueDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _weekConfiguration = Substitute.For<IWeekConfiguration>();
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay>());
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

    private PeriodOverdueDetector CreateSut(DateOnly today)
    {
        var tp = Substitute.For<TimeProvider>();
        tp.GetUtcNow().Returns(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        return new PeriodOverdueDetector(_groupRepository, _sealedDayRepository, _weekConfiguration,
            NullLogger<PeriodOverdueDetector>.Instance, tp);
    }

    private static Group MakeGroup(PaymentInterval interval, DateTime? validFrom = null, string name = "Bern") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        PaymentInterval = interval,
        ValidFrom = validFrom ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
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
    public async Task DetectAsync_MonthlyGroup_BelowThreshold_Skips()
    {
        _groupRepository.List().Returns(new List<Group> { MakeGroup(PaymentInterval.Monthly) });
        _sut = CreateSut(new DateOnly(2026, 1, 5));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_TenDaysOverdue_EmitsMediumSeverity()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodOverdueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2025, 12, 31)));
        Assert.That(evt.DaysOverdue, Is.EqualTo(10));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(evt.DedupKey, Is.EqualTo($"{group.Id}:2025-12-31"));
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_TwentyFiveDaysOverdue_EmitsHighSeverity()
    {
        _groupRepository.List().Returns(new List<Group> { MakeGroup(PaymentInterval.Monthly) });
        _sut = CreateSut(new DateOnly(2026, 1, 25));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_PeriodEndSealed_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), group.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Id = Guid.NewGuid() } });
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_WeeklyGroup_LastDayOfCurrentWeek_PreviousWeekEndIsSevenDaysOverdue()
    {
        _groupRepository.List().Returns(new List<Group> { MakeGroup(PaymentInterval.Weekly) });
        _sut = CreateSut(new DateOnly(2026, 1, 11));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodOverdueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 4)));
        Assert.That(evt.DaysOverdue, Is.EqualTo(7));
        Assert.That(evt.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_BiweeklyGroup_NineDaysOverdue_EmitsEventAnchoredOnGroupValidFrom()
    {
        var group = MakeGroup(PaymentInterval.Biweekly, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        _groupRepository.List().Returns(new List<Group> { group });
        _sut = CreateSut(new DateOnly(2026, 1, 20));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var evt = (PeriodOverdueTriggerEvent)events[0];
        Assert.That(evt.PeriodEndDate, Is.EqualTo(new DateOnly(2026, 1, 11)));
        Assert.That(evt.DaysOverdue, Is.EqualTo(9));
    }

    [Test]
    public async Task DetectAsync_GroupCreatedAfterPeriodEnd_Skips()
    {
        var group = MakeGroup(PaymentInterval.Monthly, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        _groupRepository.List().Returns(new List<Group> { group });
        _sut = CreateSut(new DateOnly(2026, 1, 10));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
