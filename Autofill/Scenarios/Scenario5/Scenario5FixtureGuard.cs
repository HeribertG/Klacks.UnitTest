// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Checks the scenario-5 fixture against the consistency conditions the specification states, before
/// any run. A broken setup has to fail as a setup error with a readable message; a red rule assertion
/// that in truth blames a fixture mistake is worse than no test at all.
/// <para>
/// Every check is pure arithmetic over the declared tables — no plan is needed and none is read. That
/// matters most for the two conditions scenario 5 adds. Whether the eleven carried-in packages leave
/// exactly the third order's day shift open follows from the tables alone, and deriving it from a
/// finished plan would make the answer depend on the very algorithm under test. The same holds for
/// blacklist conformity of the new carry-ins: a previous-month day that stands on a blacklisted shift
/// is a FIXTURE contradiction the engine could not repair even in principle, because a locked
/// assignment is exempt from stage 0.
/// </para>
/// </summary>
public static class Scenario5FixtureGuard
{
    /// <summary>
    /// Returns one message per problem; an empty result means the fixture matches the specification
    /// and the run may proceed.
    /// </summary>
    /// <param name="definition">Assembled scenario to check</param>
    public static IReadOnlyList<string> Validate(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>(AutofillCarryInGuard.Validate(definition));
        CheckRosterAndSlots(definition, problems);
        CheckOpenPackagesCoverElevenOfTwelveSlots(definition, problems);
        CheckTablesAreDisjoint(definition, problems);
        CheckFreedSlotChain(definition, problems);
        CheckCarryInsRespectTheBlacklist(definition, problems);
        CheckCommandsDoNotContradictTheCarryIn(definition, problems);
        CheckMa16NextPackageAnchor(definition, problems);
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
                "Specification finding, not a fixture error: the previous-month tables put more than one employee on "
                + "the same (order, slot kind) slot on the same February day, because a running package and a closed "
                + "package of the specification share an order and a kind and end on the same day. February is fixed "
                + "input the engine only reads per agent — it never checks a boundary slot for capacity — so the run "
                + "is unaffected, but the described February is not a plan anybody could have worked: "
                + string.Join(" | ", collisions));
        }

        notes.Add(
            "The day shift 08:00-16:00 overlaps the early shift 07:00-15:00 and starts one hour before the late shift "
            + "ends, so no employee can hold a day shift together with either on the same day. A late shift is also "
            + "followed by only 9 h of rest before the next day's day shift, below the configured 11 h, so the "
            + $"transition late to day on consecutive days is illegal by {AutofillSpecConstants.MinRestHours} h rest "
            + "and the engine is expected to avoid it. None of this is a fixture defect; it is what a fourth shift "
            + "inside the same 24 hours implies.");

        return notes;
    }

    private static void CheckRosterAndSlots(AutofillScenarioDefinition definition, List<string> problems)
    {
        if (definition.EmployeesInListOrder.Count != Scenario5SpecConstants.EmployeeCount)
        {
            problems.Add(
                $"The roster must hold {Scenario5SpecConstants.EmployeeCount.ToString(CultureInfo.InvariantCulture)} "
                + $"employees, but it holds "
                + $"{definition.EmployeesInListOrder.Count.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!definition.HasDayShifts)
        {
            problems.Add(
                "The scenario must cut a day shift per order; without it there are nine slots a day instead of twelve "
                + "and the whole scenario measures scenario 4 with two extra employees.");
        }

        if (definition.SlotsPerDay != Scenario5SpecConstants.AssignmentsPerDay)
        {
            problems.Add(
                $"The day must demand "
                + $"{Scenario5SpecConstants.AssignmentsPerDay.ToString(CultureInfo.InvariantCulture)} slots, but the "
                + $"fixture demands {definition.SlotsPerDay.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (definition.Context.Shifts.Count != Scenario5SpecConstants.TotalRequiredShifts)
        {
            problems.Add(
                $"The period must demand "
                + $"{Scenario5SpecConstants.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)} shifts, but the "
                + $"fixture demands "
                + $"{definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    /// <summary>
    /// The condition scenario 5 turns on its head compared with scenario 4: eleven of the twelve slots
    /// of the first day are held by a running package, and the twelfth — the day shift of the third
    /// order — is deliberately free, so the engine has to staff it freshly. Both halves are checked,
    /// because "eleven packages" without "and they cover these eleven slots" would pass for any eleven.
    /// </summary>
    private static void CheckOpenPackagesCoverElevenOfTwelveSlots(
        AutofillScenarioDefinition definition, List<string> problems)
    {
        var open = definition.OpenCarryIns;
        if (open.Count != Scenario5SpecConstants.OpenCarryInCount)
        {
            problems.Add(
                $"{Scenario5SpecConstants.OpenCarryInCount.ToString(CultureInfo.InvariantCulture)} employees must "
                + $"still be inside a package on {definition.PeriodFrom:yyyy-MM-dd}, but "
                + $"{open.Count.ToString(CultureInfo.InvariantCulture)} are.");
        }

        var covered = open.Select(c => (c.OrderIndex, c.Kind)).ToList();
        var uncovered = new List<string>();
        for (var order = AutofillShiftCatalog.FirstOrderIndex;
             order < AutofillShiftCatalog.FirstOrderIndex + Scenario5SpecConstants.OrderCount;
             order++)
        {
            foreach (var kind in AutofillShiftCatalog.SlotKindsOf(includeDayShift: true))
            {
                var count = covered.Count(c => c.OrderIndex == order && c.Kind == kind);
                if (count > 1)
                {
                    problems.Add(
                        $"Slot order {order.ToString(CultureInfo.InvariantCulture)} / {kind} of "
                        + $"{definition.PeriodFrom:yyyy-MM-dd} is covered by "
                        + $"{count.ToString(CultureInfo.InvariantCulture)} running packages; they would compete for "
                        + "the same single slot.");
                }
                else if (count == 0)
                {
                    uncovered.Add($"order {order.ToString(CultureInfo.InvariantCulture)} / {kind}");
                }
            }
        }

        if (uncovered.Count != Scenario5SpecConstants.UncoveredSlotsOnFirstDay)
        {
            problems.Add(
                $"Exactly "
                + $"{Scenario5SpecConstants.UncoveredSlotsOnFirstDay.ToString(CultureInfo.InvariantCulture)} slot of "
                + $"{definition.PeriodFrom:yyyy-MM-dd} must be free for the engine to staff, but "
                + $"{uncovered.Count.ToString(CultureInfo.InvariantCulture)} are: "
                + $"{(uncovered.Count == 0 ? "none" : string.Join(", ", uncovered))}.");
            return;
        }

        var expected = $"order {Scenario5SpecConstants.OrderCount.ToString(CultureInfo.InvariantCulture)} / "
            + AutofillShiftKind.Day;
        if (!string.Equals(uncovered[0], expected, StringComparison.Ordinal))
        {
            problems.Add(
                $"The one free slot of {definition.PeriodFrom:yyyy-MM-dd} must be {expected} — the specification "
                + $"leaves the third order's day shift without a carry-in on purpose — but it is {uncovered[0]}.");
        }
    }

    private static void CheckTablesAreDisjoint(AutofillScenarioDefinition definition, List<string> problems)
    {
        foreach (var group in definition.CarryIns
                     .GroupBy(c => c.AgentId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            problems.Add(
                $"{group.Key} carries {group.Count().ToString(CultureInfo.InvariantCulture)} previous-month packages; "
                + "the two tables of the specification must stay disjoint, one package per employee.");
        }

        var closed = definition.CarryIns.Count - definition.OpenCarryIns.Count;
        if (closed != Scenario5SpecConstants.ClosedCarryInCount)
        {
            problems.Add(
                $"{Scenario5SpecConstants.ClosedCarryInCount.ToString(CultureInfo.InvariantCulture)} previous-month "
                + "packages must already be closed when the period starts, but "
                + $"{closed.ToString(CultureInfo.InvariantCulture)} are.");
        }
    }

    private static void CheckFreedSlotChain(AutofillScenarioDefinition definition, List<string> problems)
    {
        var expected = Scenario5SpecConstants.FreedSlotChain
            .ToDictionary(entry => definition.PeriodFrom.AddDays(entry.DayOffset), entry => entry.FreedSlots);

        var actual = definition.OpenCarryIns
            .GroupBy(c => definition.PeriodFrom.AddDays(c.MissingDays))
            .ToDictionary(g => g.Key, g => g.Count());

        var lastCheckedDay = definition.PeriodFrom.AddDays(Scenario5SpecConstants.FreedSlotChainLastDayOffset);
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

        var total = Scenario5SpecConstants.FreedSlotChain.Sum(entry => entry.FreedSlots);
        if (definition.OpenCarryIns.Count != total)
        {
            problems.Add(
                $"The freed-slot chain accounts for {total.ToString(CultureInfo.InvariantCulture)} reoccupations, but "
                + $"{definition.OpenCarryIns.Count.ToString(CultureInfo.InvariantCulture)} packages are running.");
        }
    }

    /// <summary>
    /// The new carry-ins must not stand on a shift their own employee is blacklisted from. A
    /// previous-month day is fixed input the engine never revisits and a locked assignment is exempt
    /// from stage 0, so such a day would sit in the plan forever, be counted by the blacklist scan, and
    /// turn the fixture's own mistake into a permanent red on a rule the engine never broke.
    /// </summary>
    private static void CheckCarryInsRespectTheBlacklist(
        AutofillScenarioDefinition definition, List<string> problems)
    {
        var preferences = definition.ShiftPreferences;
        if (preferences is null || preferences.IsEmpty)
        {
            return;
        }

        foreach (var carryIn in definition.CarryIns)
        {
            var shiftRefId = AutofillShiftCatalog.ShiftIdOf(carryIn.OrderIndex, carryIn.Kind);
            if (preferences.IsBlacklisted(carryIn.AgentId, shiftRefId))
            {
                problems.Add(
                    $"The previous-month package of {carryIn.AgentId} runs on order "
                    + $"{carryIn.OrderIndex.ToString(CultureInfo.InvariantCulture)} / {carryIn.Kind}, a shift he is "
                    + "blacklisted from. Carry-in days are fixed input and locked assignments are exempt from stage 0, "
                    + "so the engine could never remove them and the blacklist assertion would be red for a fixture "
                    + "reason.");
            }
        }
    }

    /// <summary>
    /// A keyword window must not contradict a continuation the same fixture demands. The days a still
    /// running package owes the period are days the employee MUST work, and a FREE or wrong-class
    /// command on one of them asks the engine for two incompatible things at once.
    /// </summary>
    private static void CheckCommandsDoNotContradictTheCarryIn(
        AutofillScenarioDefinition definition, List<string> problems)
    {
        var commands = definition.ScheduleCommands;
        if (commands is null || commands.IsEmpty)
        {
            return;
        }

        foreach (var carryIn in definition.OpenCarryIns)
        {
            for (var offset = 0; offset < carryIn.ExpectedRemainingDays; offset++)
            {
                var day = definition.PeriodFrom.AddDays(offset);
                foreach (var command in commands.CommandsOn(carryIn.AgentId, day)
                             .Where(c => c.ForbidsKind(carryIn.Kind)))
                {
                    problems.Add(
                        $"{carryIn.AgentId} must continue his {carryIn.Kind} package on {day:yyyy-MM-dd}, but the "
                        + $"keyword {command.Keyword} of {command.FromInclusive:yyyy-MM-dd}.."
                        + $"{command.UntilInclusive:yyyy-MM-dd} forbids that shift on the same day. The fixture asks "
                        + "for two incompatible things and no plan can satisfy both.");
                }
            }
        }
    }

    /// <summary>
    /// The anchor the specification names for scenario 6: MA-16's next package can only start once his
    /// carried-in package has ended and the contractual rest days have passed. It is derived here from
    /// the tables and compared against the pinned date, so scenario 6 inherits a checked date rather
    /// than a remembered one.
    /// </summary>
    private static void CheckMa16NextPackageAnchor(AutofillScenarioDefinition definition, List<string> problems)
    {
        var carryIn = definition.CarryIns.FirstOrDefault(
            c => string.Equals(c.AgentId, Scenario5SpecConstants.DayShiftEmployees[0], StringComparison.Ordinal));
        if (carryIn is null)
        {
            problems.Add(
                $"{Scenario5SpecConstants.DayShiftEmployees[0]} must carry a previous-month package, but the fixture "
                + "gives him none.");
            return;
        }

        var lastCarriedDay = definition.PeriodFrom.AddDays(carryIn.ExpectedRemainingDays - 1);
        var earliestNextStart = lastCarriedDay.AddDays(1 + AutofillSpecConstants.MinRestDays);
        if (earliestNextStart != Scenario5SpecConstants.Ma16NextPackageStart)
        {
            problems.Add(
                $"{carryIn.AgentId}'s package ends on {lastCarriedDay:yyyy-MM-dd}, so with "
                + $"{AutofillSpecConstants.MinRestDays.ToString(CultureInfo.InvariantCulture)} rest days his next "
                + $"package could start on {earliestNextStart:yyyy-MM-dd}. The specification pins "
                + $"{Scenario5SpecConstants.Ma16NextPackageStart:yyyy-MM-dd} as the anchor scenario 6 builds on.");
        }
    }

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
