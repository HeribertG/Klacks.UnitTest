// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// The cheap half of scenario 4: everything about the fixture that can be proven without running the
/// engine. These tests are NOT explicit — they cost milliseconds, and they are the ones that catch a
/// broken fixture before anybody spends minutes of engine time on it.
/// </summary>
[TestFixture]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4FixtureGuardTests
{
    [Test]
    public void ThePinnedIdsSortOrderOneBeforeOrderThreeInEveryKind()
    {
        var problems = AutofillShiftCatalog.ValidateOrderIdOrdering();

        problems.ShouldBeEmpty(
            "With three identically cut orders the date and the start time of their slots are equal, so the shift id "
            + "is the auction's only discriminator and it is compared as an ordinal string. The nine pinned ids must "
            + "therefore sort order 1 before order 2 before order 3 inside every kind, otherwise the processing order "
            + $"written into the report is wrong: {string.Join(" | ", problems)}");

        TestContext.Out.WriteLine(
            "auction order inside one day: "
            + AutofillShiftCatalog.DescribeAuctionOrderOfOneDay(Scenario4SpecConstants.OrderCount));
    }

    [Test]
    public void TheOrderOfEveryPinnedIdResolvesBack()
    {
        var problems = new List<string>();
        for (var order = AutofillShiftCatalog.FirstOrderIndex;
             order < AutofillShiftCatalog.FirstOrderIndex + Scenario4SpecConstants.OrderCount;
             order++)
        {
            foreach (var kind in AutofillShiftCatalog.Kinds)
            {
                var resolved = AutofillShiftCatalog.OrderOf(AutofillShiftCatalog.ShiftIdOf(order, kind));
                if (resolved != order)
                {
                    problems.Add(
                        $"order {order.ToString(CultureInfo.InvariantCulture)} / {kind} resolves back to "
                        + resolved.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        problems.ShouldBeEmpty(
            "Every metric of scenario 4 recovers the order of an assignment from its shift id alone, because the "
            + $"engine carries nothing else: {string.Join(" | ", problems)}");
    }

    [Test]
    public void TheOrderLessTripleKeepsItsOwnIds()
    {
        var problems = new List<string>();
        foreach (var kind in AutofillShiftCatalog.Kinds)
        {
            var orderLess = AutofillShiftCatalog.ShiftIdOf(kind);
            if (AutofillShiftCatalog.OrderOf(orderLess) != AutofillShiftCatalog.SingleOrderIndex)
            {
                problems.Add($"{kind} of the order-less triple does not resolve to the single-order index");
            }

            for (var order = AutofillShiftCatalog.FirstOrderIndex;
                 order <= AutofillShiftCatalog.MaxOrderCount;
                 order++)
            {
                if (AutofillShiftCatalog.ShiftIdOf(order, kind) == orderLess)
                {
                    problems.Add(
                        $"order {order.ToString(CultureInfo.InvariantCulture)} / {kind} reuses the id of the "
                        + "order-less triple, so a scenario with orders and one without would address the same slot");
                }
            }
        }

        problems.ShouldBeEmpty(
            "The order dimension is opt-in and must not relabel the ids scenarios 1, 1b, 2 and 3 plan with: "
            + string.Join(" | ", problems));
    }

    [Test]
    public void TheMainFixtureSatisfiesEveryGuardCondition()
    {
        var definition = Scenario4CarryInFixture.BuildMainRun();
        var problems = Scenario4FixtureGuard.Validate(definition);

        foreach (var note in Scenario4FixtureGuard.Notes(definition))
        {
            TestContext.Out.WriteLine("fixture note: " + note);
        }

        problems.ShouldBeEmpty(
            "The scenario-4 fixture must satisfy all four consistency conditions of the specification before any run: "
            + string.Join(" | ", problems));
    }

    [Test]
    public void TheSymmetryFixtureSatisfiesEveryGuardCondition()
    {
        var definition = Scenario4CarryInFixture.BuildSymmetryRun();
        var problems = Scenario4FixtureGuard.Validate(definition);

        problems.ShouldBeEmpty(
            "Swapping the id triples of the outer orders must not break a single fixture condition; if it does, the "
            + $"symmetry run would be a different scenario rather than a relabelling: {string.Join(" | ", problems)}");
    }

    [Test]
    public void TheFixtureDemandsExactlyTheSpecifiedShiftsAndEmployees()
    {
        var definition = Scenario4CarryInFixture.BuildMainRun();
        var problems = new List<string>();

        if (definition.Context.Shifts.Count != Scenario4SpecConstants.TotalRequiredShifts)
        {
            problems.Add(
                $"{definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)} demanded shifts instead of "
                + Scenario4SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture));
        }

        if (definition.Context.Agents.Count != Scenario4SpecConstants.EmployeeCount)
        {
            problems.Add(
                $"{definition.Context.Agents.Count.ToString(CultureInfo.InvariantCulture)} employees instead of "
                + Scenario4SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture));
        }

        var oversuppliedSlots = definition.Context.Shifts
            .GroupBy(s => (s.Id, s.Date))
            .Where(g => g.Count() > 1)
            .ToList();
        if (oversuppliedSlots.Count > 0)
        {
            problems.Add(
                $"{oversuppliedSlots.Count.ToString(CultureInfo.InvariantCulture)} (shift id, date) pairs occur more "
                + "than once, which the evaluation context would collapse into one slot of higher capacity");
        }

        var unpadded = definition.EmployeesInListOrder
            .Where(e => !Scenario4SpecConstants.Employees.Contains(e, StringComparer.Ordinal))
            .ToList();
        if (unpadded.Count > 0)
        {
            problems.Add($"unexpected employee ids: {string.Join(", ", unpadded)}");
        }

        var ordinal = definition.EmployeesInListOrder.OrderBy(e => e, StringComparer.Ordinal).ToList();
        if (!ordinal.SequenceEqual(definition.EmployeesInListOrder, StringComparer.Ordinal))
        {
            problems.Add(
                "the employee ids are not in ordinal order, so every artifact sorted by id would list the roster in a "
                + "different order than the list rank");
        }

        problems.ShouldBeEmpty(
            "The scenario-4 demand is three orders of three shifts over 31 days, staffed from a roster of fifteen "
            + $"zero-padded ids: {string.Join(" | ", problems)}");
    }

    [Test]
    public void EveryEmployeeCarriesExactlyOnePreviousMonthPackage()
    {
        var definition = Scenario4CarryInFixture.BuildMainRun();
        var withoutCarryIn = definition.EmployeesInListOrder
            .Where(e => !definition.CarryIns.Any(c => string.Equals(c.AgentId, e, StringComparison.Ordinal)))
            .ToList();

        withoutCarryIn.ShouldBeEmpty(
            "The two specification tables together cover all fifteen employees, nine running and six closed; an "
            + $"employee in neither would start the period from nowhere: {string.Join(", ", withoutCarryIn)}");

        definition.CarryIns.Count.ShouldBe(
            Scenario4SpecConstants.EmployeeCount,
            "each employee carries exactly one previous-month package");
    }

    [Test]
    public void TheRunWithoutCarryInHasNoPreviousMonth()
    {
        var definition = Scenario4CarryInFixture.BuildWithoutCarryIn();

        definition.CarryIns.ShouldBeEmpty("S4d isolates the parallel orders from the month transition");
        definition.Context.BoundaryLockedWorks.ShouldBeEmpty(
            "without a previous month the engine must see no boundary work at all");
        definition.Context.Shifts.Count.ShouldBe(
            Scenario4SpecConstants.TotalRequiredShifts,
            "the demand is the same as in the main run; only the previous month is gone");
    }
}
