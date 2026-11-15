// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Owner rule (2026-11-15): the container is the unit that is planned. A shift day the shift schedule marks as a container
/// child (get_shift_schedule.is_in_template_container) gets no seat of its own in the automatic planning; the container
/// shift keeps its seat, and a shift taken out of the container is plannable again. The marker comes from the same
/// schedule rows the shift table shows, so the seats per shift day equal ShiftStaffingDemand.RequiredOn of the row.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class WizardShiftBuilderContainerChildTests
{
    private static readonly DateOnly Monday = new(2026, 4, 20);
    private static readonly DateOnly Tuesday = Monday.AddDays(1);

    private DataBaseContext _context = null!;
    private IShiftScheduleReader _scheduleReader = null!;
    private List<ShiftDayAssignment> _scheduleRows = null!;
    private WizardShiftBuilder _sut = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _scheduleRows = [];
        _scheduleReader = Substitute.For<IShiftScheduleReader>();
        _scheduleReader
            .GetShiftScheduleListAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<DateOnly>?>(), Arg.Any<List<Guid>?>(),
                Arg.Any<bool>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<ShiftDayAssignment>)_scheduleRows.ToList());
        _sut = new WizardShiftBuilder(_context, _scheduleReader);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task BuildAsync_ContainerChildDay_GetsNoSeat_ContainerKeepsExactlyOne()
    {
        var container = AddShift(Monday, Monday, ShiftType.IsContainer);
        var child = AddShift(Monday, Monday, ShiftType.IsTask);
        await _context.SaveChangesAsync();
        AddRow(container, Monday, isInContainer: false);
        AddRow(child, Monday, isInContainer: true);

        var result = await _sut.BuildAsync(null, Monday, Monday, null, CancellationToken.None);

        SeatsOf(result, container.Id, Monday).ShouldBe(1);
        SeatsOf(result, child.Id, Monday).ShouldBe(0);
    }

    [Test]
    public async Task BuildAsync_ShiftInContainerOnOneDayOnly_KeepsItsSeatOnTheOtherDay()
    {
        var child = AddShift(Monday, Tuesday, ShiftType.IsTask);
        await _context.SaveChangesAsync();
        AddRow(child, Monday, isInContainer: true);
        AddRow(child, Tuesday, isInContainer: false);

        var result = await _sut.BuildAsync(null, Monday, Tuesday, null, CancellationToken.None);

        SeatsOf(result, child.Id, Monday).ShouldBe(0);
        SeatsOf(result, child.Id, Tuesday).ShouldBe(1);
    }

    [Test]
    public async Task BuildAsync_ShiftTakenOutOfTheContainer_GetsItsSeatBack()
    {
        var formerChild = AddShift(Monday, Monday, ShiftType.IsTask);
        await _context.SaveChangesAsync();
        AddRow(formerChild, Monday, isInContainer: false);

        var result = await _sut.BuildAsync(null, Monday, Monday, null, CancellationToken.None);

        SeatsOf(result, formerChild.Id, Monday).ShouldBe(1);
    }

    [Test]
    public async Task BuildAsync_SeatsPerShiftDay_EqualTheStaffingDemandOfTheShiftTableRow()
    {
        var container = AddShift(Monday, Tuesday, ShiftType.IsContainer);
        var child = AddShift(Monday, Tuesday, ShiftType.IsTask);
        var regular = AddShift(Monday, Tuesday, ShiftType.IsTask, quantity: 2, sumEmployees: 3);
        var sporadic = AddShift(Monday, Tuesday, ShiftType.IsTask);
        sporadic.IsSporadic = true;
        await _context.SaveChangesAsync();
        foreach (var day in new[] { Monday, Tuesday })
        {
            AddRow(container, day, isInContainer: false);
            AddRow(child, day, isInContainer: day == Monday);
            AddRow(regular, day, isInContainer: false);
            AddRow(sporadic, day, isInContainer: false);
        }

        var result = await _sut.BuildAsync(null, Monday, Tuesday, null, CancellationToken.None);

        foreach (var row in _scheduleRows)
        {
            SeatsOf(result, row.ShiftId, row.Date).ShouldBe(
                ShiftStaffingDemand.RequiredOn(row), $"shift {row.ShiftId} on {row.Date}");
        }
    }

    [Test]
    public async Task BuildAsync_ReadsTheMarkerForThePeriodAndScenarioUnrestricted()
    {
        var token = Guid.NewGuid();
        var shift = AddShift(Monday, Tuesday, ShiftType.IsTask);
        shift.AnalyseToken = token;
        await _context.SaveChangesAsync();

        await _sut.BuildAsync(null, Monday, Tuesday, token, CancellationToken.None);

        await _scheduleReader.Received(1).GetShiftScheduleListAsync(
            Monday, Tuesday, null, null, false, token, CancellationToken.None);
    }

    private Shift AddShift(DateOnly from, DateOnly until, ShiftType shiftType, int quantity = 1, int sumEmployees = 1)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = shiftType.ToString(),
            Abbreviation = "S",
            FromDate = from,
            UntilDate = until,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(15, 30),
            WorkTime = 7.5m,
            Quantity = quantity,
            SumEmployees = sumEmployees,
            ShiftType = shiftType,
            Status = ShiftStatus.OriginalShift,
            IsMonday = true,
            IsTuesday = true,
        };
        _context.Shift.Add(shift);
        return shift;
    }

    private void AddRow(Shift shift, DateOnly date, bool isInContainer) => _scheduleRows.Add(new ShiftDayAssignment
    {
        ShiftId = shift.Id,
        Date = date,
        IsSporadic = shift.IsSporadic,
        ShiftType = (int)shift.ShiftType,
        Status = (int)shift.Status,
        IsInTemplateContainer = isInContainer,
        Quantity = shift.Quantity,
        SumEmployees = shift.SumEmployees,
    });

    private static int SeatsOf(IReadOnlyList<Klacks.ScheduleOptimizer.Models.CoreShift> seats, Guid shiftId, DateOnly date)
        => seats.Count(seat => seat.Id == shiftId.ToString() && seat.Date == date.ToString(DateTimeFormats.IsoDate, CultureInfo.InvariantCulture));
}
