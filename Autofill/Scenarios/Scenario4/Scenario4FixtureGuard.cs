// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Checks the scenario-4 fixture against the four consistency conditions the specification states,
/// before any run. A broken setup has to fail as a setup error with a readable message; a red rule
/// assertion that in truth blames a fixture mistake is worse than no test at all.
/// <para>
/// Every check is pure arithmetic over the declared tables — no plan is needed and none is read. That
/// is deliberate for the fourth one especially: whether the extended free days of the two lowest ranks
/// are forced follows from the previous-month end dates and the occupancy of 1 March, and deriving it
/// from a finished plan instead would make the answer depend on the very algorithm under test.
/// </para>
/// </summary>
public static class Scenario4FixtureGuard
{
    /// <summary>
    /// Returns one message per problem; an empty result means the fixture matches the specification
    /// and the run may proceed.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    public static IReadOnlyList<string> Validate(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>(AutofillCarryInGuard.Validate(definition));
        CheckOpenPackagesCoverEverySlot(definition, problems);
        CheckTablesAreDisjoint(definition, problems);
        CheckFreedSlotChain(definition, problems);
        CheckForcedIdleEmployees(definition, problems);
        return problems;
    }

    /// <summary>
    /// Observations the guard reports but does not fail on — findings about the specification itself
    /// rather than about the fixture's faithfulness to it.
    /// </summary>
    /// <param name="definition">Assembled scenario to describe</param>
    public static IReadOnlyList<string> Notes(AutofillScenarioDefinition definition)
    {
        var notes = new List<string>();
        var collisions = new List<string>();

        foreach (var group in definition.CarryIns.GroupBy(c => (c.OrderIndex, c.Kind)))
        {
            var byDay = new Dictionary<DateOnly, List<string>>();
            foreach (var carryIn in group)
            {
                foreach (var day in carryIn.Days())
                {
                    if (!byDay.TryGetValue(day, out var holders))
                    {
                        holders = [];
                        byDay[day] = holders;
                    }

                    holders.Add(carryIn.AgentId);
                }
            }

            foreach (var day in byDay.Where(d => d.Value.Count > 1).OrderBy(d => d.Key))
            {
                collisions.Add(
                    $"order {group.Key.OrderIndex.ToString(CultureInfo.InvariantCulture)} {group.Key.Kind} on "
                    + $"{day.Key:yyyy-MM-dd}: {string.Join(", ", day.Value.OrderBy(a => a, StringComparer.Ordinal))}");
            }
        }

        if (collisions.Count > 0)
        {
            notes.Add(
                "Specification finding, not a fixture error: the two previous-month tables put more than one employee "
                + "on the same (order, shift kind) slot on the same February day, because a running package and a "
                + "closed package of the specification share an order and a kind and end on the same day. February is "
                + "fixed input the engine only reads per agent — it never checks a boundary slot for capacity — so the "
                + "run is unaffected, but the described February is not a plan anybody could have worked: "
                + string.Join(" | ", collisions));
        }

        return notes;
    }

    private static void CheckOpenPackagesCoverEverySlot(
        AutofillScenarioDefinition definition, List<string> problems)
    {
        var open = definition.OpenCarryIns;
        if (open.Count != Scenario4SpecConstants.OpenCarryInCount)
        {
            problems.Add(
                $"{Scenario4SpecConstants.OpenCarryInCount.ToString(CultureInfo.InvariantCulture)} employees must "
                + $"still be inside a package on {definition.PeriodFrom:yyyy-MM-dd}, but "
                + $"{open.Count.ToString(CultureInfo.InvariantCulture)} are.");
        }

        var covered = open.Select(c => (c.OrderIndex, c.Kind)).ToList();
        for (var order = AutofillShiftCatalog.FirstOrderIndex;
             order < AutofillShiftCatalog.FirstOrderIndex + Scenario4SpecConstants.OrderCount;
             order++)
        {
            foreach (var kind in AutofillShiftCatalog.Kinds)
            {
                var count = covered.Count(c => c.OrderIndex == order && c.Kind == kind);
                if (count != 1)
                {
                    problems.Add(
                        $"Slot order {order.ToString(CultureInfo.InvariantCulture)} / {kind} of "
                        + $"{definition.PeriodFrom:yyyy-MM-dd} must be covered by exactly one running package, but "
                        + $"{count.ToString(CultureInfo.InvariantCulture)} cover it.");
                }
            }
        }
    }

    private static void CheckTablesAreDisjoint(AutofillScenarioDefinition definition, List<string> problems)
    {
        foreach (var group in definition.CarryIns.GroupBy(c => c.AgentId, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add(
                $"{group.Key} carries {group.Count().ToString(CultureInfo.InvariantCulture)} previous-month packages; "
                + "the two tables of the specification must stay disjoint, one package per employee.");
        }

        var closed = definition.CarryIns.Count - definition.OpenCarryIns.Count;
        if (closed != Scenario4SpecConstants.ClosedCarryInCount)
        {
            problems.Add(
                $"{Scenario4SpecConstants.ClosedCarryInCount.ToString(CultureInfo.InvariantCulture)} previous-month "
                + $"packages must already be closed when the period starts, but "
                + $"{closed.ToString(CultureInfo.InvariantCulture)} are.");
        }
    }

    private static void CheckFreedSlotChain(AutofillScenarioDefinition definition, List<string> problems)
    {
        var expected = Scenario4SpecConstants.FreedSlotChain
            .ToDictionary(entry => definition.PeriodFrom.AddDays(entry.DayOffset), entry => entry.FreedSlots);

        var actual = definition.OpenCarryIns
            .GroupBy(c => definition.PeriodFrom.AddDays(c.MissingDays))
            .ToDictionary(g => g.Key, g => g.Count());

        var lastCheckedDay = definition.PeriodFrom.AddDays(Scenario4SpecConstants.FreedSlotChainLastDayOffset);
        for (var day = definition.PeriodFrom; day <= lastCheckedDay; day = day.AddDays(1))
        {
            expected.TryGetValue(day, out var wanted);
            actual.TryGetValue(day, out var got);
            if (wanted != got)
            {
                problems.Add(
                    $"On {day:yyyy-MM-dd} the specification frees "
                    + $"{wanted.ToString(CultureInfo.InvariantCulture)} slot(s), but the fixture frees "
                    + $"{got.ToString(CultureInfo.InvariantCulture)}. Freed on that day: "
                    + $"{DescribeFreedOn(definition, day)}.");
            }
        }

        var total = Scenario4SpecConstants.FreedSlotChain.Sum(entry => entry.FreedSlots);
        if (definition.OpenCarryIns.Count != total)
        {
            problems.Add(
                $"The freed-slot chain accounts for {total.ToString(CultureInfo.InvariantCulture)} reoccupations, but "
                + $"{definition.OpenCarryIns.Count.ToString(CultureInfo.InvariantCulture)} packages are running.");
        }
    }

    /// <summary>
    /// The fourth condition: which employees could work from the first day of the period and still
    /// find no slot. An employee is available again <see cref="AutofillSpecConstants.MinRestDays"/>
    /// days after his package ended; the nine running packages hold every slot of the first day, so
    /// every available employee without a running package is idle for a reason he did not choose.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    /// <param name="problems">Collected problem messages</param>
    private static void CheckForcedIdleEmployees(AutofillScenarioDefinition definition, List<string> problems)
    {
        var slotsPerDay = AutofillSpecConstants.ShiftsPerDay * definition.OrderCount;
        var freeSlotsOnFirstDay = slotsPerDay - definition.OpenCarryIns.Count;
        if (freeSlotsOnFirstDay != 0)
        {
            problems.Add(
                $"{freeSlotsOnFirstDay.ToString(CultureInfo.InvariantCulture)} slot(s) of "
                + $"{definition.PeriodFrom:yyyy-MM-dd} are not held by a running package, so the extended free days of "
                + "the lowest ranks would no longer be forced and the fourth guard condition of the specification "
                + "would not hold.");
            return;
        }

        var openEmployees = definition.OpenCarryIns.Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var forcedIdle = definition.CarryIns
            .Where(c => !openEmployees.Contains(c.AgentId))
            .Where(c => EarliestLegalStart(c) <= definition.PeriodFrom)
            .Select(c => c.AgentId)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        var expected = Scenario4SpecConstants.ForcedIdleOnFirstDay.OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (!forcedIdle.SequenceEqual(expected, StringComparer.Ordinal))
        {
            problems.Add(
                $"The employees whose free days at the start of {definition.PeriodFrom:yyyy-MM-dd} are forced must be "
                + $"[{string.Join(", ", expected)}], but the previous-month end dates make them "
                + $"[{string.Join(", ", forcedIdle)}]. Earliest legal start per closed package: "
                + string.Join(
                    ", ",
                    definition.CarryIns
                        .Where(c => !openEmployees.Contains(c.AgentId))
                        .OrderBy(c => c.AgentId, StringComparer.Ordinal)
                        .Select(c => $"{c.AgentId}={EarliestLegalStart(c):yyyy-MM-dd}")));
        }
    }

    /// <summary>
    /// First day an employee may work again after a closed package: the day after the package plus the
    /// contractual rest days.
    /// </summary>
    /// <param name="carryIn">Closed previous-month package</param>
    private static DateOnly EarliestLegalStart(AutofillCarryIn carryIn)
        => carryIn.PackageEndInclusive.AddDays(1 + AutofillSpecConstants.MinRestDays);

    private static string DescribeFreedOn(AutofillScenarioDefinition definition, DateOnly day)
    {
        var employees = definition.OpenCarryIns
            .Where(c => definition.PeriodFrom.AddDays(c.MissingDays) == day)
            .Select(c => c.AgentId)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToList();

        return employees.Count == 0 ? "none" : string.Join(", ", employees);
    }
}
