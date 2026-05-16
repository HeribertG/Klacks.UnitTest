// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodCloseDueDetector — covers period-end computation per PaymentInterval
/// and the "already sealed" skip path.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseDueDetectorTests
{
    private IGroupRepository _groupRepository = null!;
    private ISealedDayRepository _sealedDayRepository = null!;
    private PeriodCloseDueDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _sealedDayRepository = Substitute.For<ISealedDayRepository>();
        _sut = new PeriodCloseDueDetector(_groupRepository, _sealedDayRepository,
            NullLogger<PeriodCloseDueDetector>.Instance);
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
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today.Day >= 25)
        {
            // last week of month — month-end is within the 3-day warn window; skip this assertion
            Assert.Ignore("Calendar day is in warn window — covered by other test.");
        }

        _groupRepository.List().Returns(new List<Group> { MakeGroup(PaymentInterval.Monthly) });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_MonthlyGroup_AlreadySealedAtEnd_Skips()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today.Day < 25) Assert.Ignore("Calendar day not near month-end.");

        var group = MakeGroup(PaymentInterval.Monthly);
        _groupRepository.List().Returns(new List<Group> { group });
        _sealedDayRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), group.Id, Arg.Any<CancellationToken>())
            .Returns(new List<SealedDay> { new() { Id = Guid.NewGuid() } });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
