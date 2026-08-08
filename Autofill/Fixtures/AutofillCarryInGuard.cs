// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// Checks a carry-in fixture for internal consistency BEFORE the autofill runs. The point is to make
/// a broken fixture fail as a fixture error with a readable message, instead of turning into a red
/// rule assertion that blames the algorithm for a setup mistake.
/// </summary>
public static class AutofillCarryInGuard
{
    /// <summary>
    /// Returns one message per problem found; an empty result means the fixture is consistent.
    /// Checked: every package lies before the period, is not longer than its target, its remaining
    /// days match its served days, packages that are already closed expect no continuation, the
    /// employees still inside a package on the first day of the period carry distinct shift kinds,
    /// and there are never more of them than the day has shifts.
    /// </summary>
    /// <param name="definition">Scenario to check</param>
    public static IReadOnlyList<string> Validate(AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        var periodFrom = definition.PeriodFrom;

        foreach (var carryIn in definition.CarryIns)
        {
            var who = $"Carry-in of {carryIn.AgentId}";

            if (carryIn.PackageEndInclusive >= periodFrom)
            {
                problems.Add($"{who} ends on {carryIn.PackageEndInclusive:yyyy-MM-dd}, which is not before the period start {periodFrom:yyyy-MM-dd}.");
            }

            if (carryIn.PackageStart > carryIn.PackageEndInclusive)
            {
                problems.Add($"{who} starts on {carryIn.PackageStart:yyyy-MM-dd} after it ends on {carryIn.PackageEndInclusive:yyyy-MM-dd}.");
            }

            if (carryIn.ServedDays > carryIn.TargetPackageLength)
            {
                problems.Add($"{who} already served {carryIn.ServedDays} days but its target length is only {carryIn.TargetPackageLength}.");
            }

            var open = carryIn.IsOpenAt(periodFrom);
            var expectedRemaining = open ? carryIn.MissingDays : 0;
            if (carryIn.ExpectedRemainingDays != expectedRemaining)
            {
                problems.Add(
                    $"{who} declares {carryIn.ExpectedRemainingDays} remaining in-period days, but {expectedRemaining} follow from "
                    + $"served={carryIn.ServedDays}, target={carryIn.TargetPackageLength}, open at period start={open}.");
            }

            if (open && carryIn.ExpectedFirstShiftKind != carryIn.Kind)
            {
                problems.Add(
                    $"{who} is still running at the period start, so its first in-period shift must stay {carryIn.Kind}, "
                    + $"but the fixture expects {carryIn.ExpectedFirstShiftKind}.");
            }

            if (!open && carryIn.ExpectedFirstShiftKind == carryIn.Kind)
            {
                problems.Add(
                    $"{who} is closed before the period, so the next package must rotate away from {carryIn.Kind}, "
                    + "but the fixture expects the same shift kind again.");
            }
        }

        var openCarryIns = definition.OpenCarryIns;
        var duplicateKinds = openCarryIns
            .GroupBy(c => c.Kind)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var kind in duplicateKinds)
        {
            problems.Add(
                $"More than one employee is still inside a {kind} package on {periodFrom:yyyy-MM-dd}; they would compete for the same single slot.");
        }

        if (openCarryIns.Count > AutofillSpecConstants.ShiftsPerDay)
        {
            problems.Add(
                $"{openCarryIns.Count} employees are still inside a package on {periodFrom:yyyy-MM-dd}, but the day only has "
                + $"{AutofillSpecConstants.ShiftsPerDay} shifts.");
        }

        return problems;
    }
}
