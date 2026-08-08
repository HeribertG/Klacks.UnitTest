// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// One employee's work package from the month before the planning period, together with what the
/// autofill is expected to make of it. Purely declarative: the builder turns it into boundary works
/// and the analyzer compares the plan against the expectations recorded here.
/// </summary>
/// <param name="AgentId">Employee the package belongs to</param>
/// <param name="Kind">Shift kind of every day of the package</param>
/// <param name="PackageStart">First day of the package, always before the period</param>
/// <param name="PackageEndInclusive">Last day of the package that lies before the period</param>
/// <param name="TargetPackageLength">Length the package is supposed to reach, normally MaxWorkDays</param>
/// <param name="ExpectedFirstShiftKind">Shift kind the employee's first in-period shift must have</param>
/// <param name="ExpectedRemainingDays">In-period days the package must still run; 0 when it is already closed</param>
public sealed record AutofillCarryIn(
    string AgentId,
    AutofillShiftKind Kind,
    DateOnly PackageStart,
    DateOnly PackageEndInclusive,
    int TargetPackageLength,
    AutofillShiftKind ExpectedFirstShiftKind,
    int ExpectedRemainingDays)
{
    /// <summary>Days of the package that already lie in the previous month.</summary>
    public int ServedDays => PackageEndInclusive.DayNumber - PackageStart.DayNumber + 1;

    /// <summary>Days still missing to reach <see cref="TargetPackageLength"/>.</summary>
    public int MissingDays => Math.Max(0, TargetPackageLength - ServedDays);

    /// <summary>
    /// True when the package is still running on the first day of the period: it ends on the day
    /// before the period and has not yet reached its target length.
    /// </summary>
    /// <param name="periodFrom">First day of the planning period</param>
    public bool IsOpenAt(DateOnly periodFrom)
        => PackageEndInclusive == periodFrom.AddDays(-1) && MissingDays > 0;

    /// <summary>All calendar days the package occupies before the period.</summary>
    public IEnumerable<DateOnly> Days()
    {
        for (var date = PackageStart; date <= PackageEndInclusive; date = date.AddDays(1))
        {
            yield return date;
        }
    }
}
