// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A run of free days at the START of an employee's period that the plan left no way to fill: on every
/// one of those days there was no unfilled slot the employee could have taken without breaking a hard
/// rule. Derived from the finished plan, because the plan itself records no such flag — the known
/// limit "forced is always false".
/// <para>
/// Deliberately restricted to the leading free days, before the employee's first package. Free days
/// between two packages are what the 5/2 rhythm is made of and would drown the measure in noise; the
/// question this measure answers is whether an employee was kept out of the plan at the start, not
/// whether the plan gives people weekends.
/// </para>
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="DateFrom">First free day of the run</param>
/// <param name="DateTo">Last free day of the run, inclusive</param>
/// <param name="Cause">Why no day of the run could be filled</param>
public sealed record ForcedFreeDayRun(string Employee, DateOnly DateFrom, DateOnly DateTo, string Cause)
{
    /// <summary>Cause text for a run whose every day was already fully staffed by other employees.</summary>
    public const string NoOpenSlotCause = "every slot of those days was already staffed by somebody else";

    /// <summary>Cause text for a run whose open slots the employee could not legally have taken.</summary>
    public const string NoLegalOpenSlotCause = "the open slots of those days were closed to the employee by a hard rule";
}
