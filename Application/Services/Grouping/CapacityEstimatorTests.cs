// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins F7: peak concurrent demand per unit and weekday (sum of quantities of overlapping shifts, ends
/// exclusive, cross-midnight shifts extended past 24:00) against the clients in scope eligible for at
/// least one peak shift on that weekday.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class CapacityEstimatorTests
{
    private static readonly DateOnly Monday = new(2026, 6, 15);
    private static readonly DateOnly Tuesday = new(2026, 6, 16);
    private static readonly Guid Unit = Guid.NewGuid();
    private static readonly GroupingGroupTree Tree = new([new GroupingGroupRecord(Unit, "Unit", null, null, null, null)]);

    private readonly Dictionary<Guid, GroupingShiftRecord> _shifts = new();
    private readonly Dictionary<Guid, IReadOnlyList<DateOnly>> _runDays = new();
    private GroupingMembershipState _state = null!;
    private FakeEligibilityOracle _oracle = null!;

    [SetUp]
    public void SetUp()
    {
        _shifts.Clear();
        _runDays.Clear();
        _state = GroupingMembershipState.From([], Tree, new HashSet<Guid>(), new HashSet<Guid>());
        _oracle = new FakeEligibilityOracle();
    }

    private Guid Shift(int startHour, int endHour, int quantity, params DateOnly[] days) =>
        Shift(startHour, endHour, quantity, 1, days);

    private Guid Shift(int startHour, int endHour, int quantity, int sumEmployees, params DateOnly[] days)
    {
        var id = Guid.NewGuid();
        _shifts[id] = new GroupingShiftRecord(id, $"S{startHour}", new TimeOnly(startHour, 0), new TimeOnly(endHour, 0), quantity)
        {
            SumEmployees = sumEmployees,
        };
        _runDays[id] = days.Length == 0 ? [Monday] : days;
        _state.AddShift(id, Unit);
        return id;
    }

    private Guid Client(params Guid[] eligibleShifts)
    {
        var id = Guid.NewGuid();
        _state.AddClient(id, Unit);
        foreach (var shift in eligibleShifts)
        {
            _oracle.Allow(id, shift);
        }

        return id;
    }

    private IReadOnlyList<GroupingFinding> Estimate() =>
        new CapacityEstimator().Estimate(new CapacityInput(Tree, _state, _shifts, _runDays, _oracle, null));

    [Test]
    public void OverlappingDemandAboveSupply_IsReported()
    {
        var early = Shift(6, 14, 2);
        var mid = Shift(10, 18, 1);
        Client(early, mid);
        Client(early);

        var finding = Estimate().Single();

        finding.Code.ShouldBe(GroupingFindingCode.CapacityShortfall);
        finding.Weekday.ShouldBe(DayOfWeek.Monday);
        finding.Demand.ShouldBe(3);
        finding.Supply.ShouldBe(2);
        finding.ReportOnly.ShouldBeTrue();
    }

    [Test]
    public void SequentialShifts_DoNotAddUp()
    {
        var early = Shift(6, 14, 1);
        var late = Shift(14, 22, 1);
        Client(early, late);

        Estimate().ShouldBeEmpty();
    }

    [Test]
    public void CrossMidnightShift_OverlapsTheLateShift()
    {
        var late = Shift(15, 23, 1);
        var night = Shift(22, 6, 1);
        Client(late, night);

        Estimate().Single().Demand.ShouldBe(2);
    }

    [Test]
    public void OnlyWeekdaysOnWhichAShiftRuns_AreChecked()
    {
        var mondayOnly = Shift(6, 14, 2, Monday);
        var tuesdayOnly = Shift(6, 14, 1, Tuesday);
        Client(mondayOnly, tuesdayOnly);

        Estimate().Single().Weekday.ShouldBe(DayOfWeek.Monday);
    }

    [Test]
    public void QuantityZero_CountsAsOne()
    {
        var shift = Shift(6, 14, 0);
        Client(shift);

        Estimate().ShouldBeEmpty();
    }

    [Test]
    public void DemandIsQuantityTimesSumEmployees()
    {
        var shift = Shift(6, 14, 2, sumEmployees: 3);
        Client(shift);
        Client(shift);
        Client(shift);
        Client(shift);
        Client(shift);

        var finding = Estimate().Single();

        finding.Demand.ShouldBe(6);
        finding.Supply.ShouldBe(5);
    }

    [Test]
    public void SumEmployeesZero_CountsAsOne()
    {
        var shift = Shift(6, 14, 1, sumEmployees: 0);
        Client(shift);

        Estimate().ShouldBeEmpty();
    }

    [Test]
    public void SupplyCountsOnlyClientsEligibleOnThatWeekday()
    {
        var shift = Shift(6, 14, 1);
        var client = Guid.NewGuid();
        _state.AddClient(client, Unit);
        _oracle.Allow(client, shift, DayOfWeek.Tuesday);

        Estimate().Single().Supply.ShouldBe(0);
    }
}
