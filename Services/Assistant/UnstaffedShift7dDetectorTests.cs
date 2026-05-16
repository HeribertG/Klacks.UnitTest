// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UnstaffedShift7dDetector — covers empty-result, only-staffed,
/// mix of staffed/understaffed, and the daysUntil severity boundary.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Domain.Models.Schedules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UnstaffedShift7dDetectorTests
{
    private IShiftScheduleRepository _repo = null!;
    private UnstaffedShift7dDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IShiftScheduleRepository>();
        _sut = new UnstaffedShift7dDetector(_repo, NullLogger<UnstaffedShift7dDetector>.Instance);
    }

    private static ShiftDayAssignment MakeAssignment(DateOnly date, int sum, int quantity, Guid? id = null) => new()
    {
        ShiftId = id ?? Guid.NewGuid(),
        Date = date,
        Quantity = quantity,
        SumEmployees = sum,
        ShiftName = "Test",
        Abbreviation = "T"
    };

    [Test]
    public async Task DetectAsync_NoAssignments_ReturnsEmpty()
    {
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>(), 0));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_OnlyFullyStaffed_ReturnsEmpty()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(1), sum: 3, quantity: 3),
                MakeAssignment(today.AddDays(2), sum: 2, quantity: 2)
            }, 2));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_UnderstaffedShift_EmitsEvent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(2), sum: 1, quantity: 3)
            }, 1));

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var unstaffed = events.Single() as UnstaffedShiftTriggerEvent;
        Assert.That(unstaffed, Is.Not.Null);
        Assert.That(unstaffed!.DaysUntil, Is.EqualTo(2));
    }

    [Test]
    public async Task DetectAsync_MixedDays_RanksSeverityCorrectly()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(1), sum: 0, quantity: 1),
                MakeAssignment(today.AddDays(5), sum: 0, quantity: 1)
            }, 2));

        var events = (await _sut.DetectAsync()).Cast<UnstaffedShiftTriggerEvent>().ToList();

        Assert.That(events.Single(e => e.DaysUntil == 1).Severity, Is.EqualTo("high"));
        Assert.That(events.Single(e => e.DaysUntil == 5).Severity, Is.EqualTo("medium"));
    }

    [Test]
    public async Task DetectAsync_IgnoresZeroQuantitySlots()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _repo.GetShiftScheduleAsync(Arg.Any<ShiftScheduleFilter>(), Arg.Any<CancellationToken>())
            .Returns((new List<ShiftDayAssignment>
            {
                MakeAssignment(today.AddDays(1), sum: 0, quantity: 0)
            }, 1));

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
