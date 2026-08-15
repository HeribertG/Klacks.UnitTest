// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// One per-day planning command of one employee, stated as the WINDOW the specification writes rather
/// than as the per-day rows the engine stores.
/// <para>
/// The engine's record is <c>CoreScheduleCommand(string AgentId, DateOnly Date, ScheduleCommandKeyword
/// Keyword)</c> — one row per calendar day, no window of its own. A window of three days is three
/// rows, and <see cref="ToCore"/> is the expansion. Semantics, from the enforcement code: <c>Free</c>
/// forbids every shift on the day, <c>OnlyEarly</c> / <c>OnlyLate</c> / <c>OnlyNight</c> forbid every
/// shift whose CLASS is not the named one, and the <c>No*</c> keywords forbid the named class. None of
/// them forces an assignment — a keyword can only ever narrow what the day may hold.
/// </para>
/// <para>
/// The class a command compares against is the engine's shift type index, so the day shift 08:00-16:00
/// counts as LATE here: it is permitted under <c>OnlyLate</c> and forbidden under <c>NoLate</c>.
/// </para>
/// </summary>
/// <param name="AgentId">Employee the command applies to</param>
/// <param name="Keyword">The command keyword</param>
/// <param name="FromInclusive">First day of the window</param>
/// <param name="UntilInclusive">Last day of the window</param>
public sealed record AutofillScheduleCommand(
    string AgentId,
    ScheduleCommandKeyword Keyword,
    DateOnly FromInclusive,
    DateOnly UntilInclusive)
{
    /// <summary>Number of calendar days the window spans.</summary>
    public int LengthDays => UntilInclusive.DayNumber - FromInclusive.DayNumber + 1;

    /// <summary>True when the window covers that day.</summary>
    /// <param name="date">Calendar day to test</param>
    public bool Covers(DateOnly date) => date >= FromInclusive && date <= UntilInclusive;

    /// <summary>All calendar days of the window.</summary>
    public IEnumerable<DateOnly> Days()
    {
        for (var date = FromInclusive; date <= UntilInclusive; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    /// <summary>The engine rows behind this window: one per day.</summary>
    public IEnumerable<CoreScheduleCommand> ToCore()
        => Days().Select(date => new CoreScheduleCommand(AgentId, date, Keyword));

    /// <summary>
    /// Whether a shift of that class breaks this keyword. Mirrors the engine's own decision
    /// (<c>Stage0HardConstraintChecker.CheckKeyword</c>, <c>PlanConstraintChecker.ViolatesKeyword</c>)
    /// rather than restating it: <c>NotFree</c> is a no-op in every one of the engine's switches, so it
    /// is a no-op here too.
    /// </summary>
    /// <param name="shiftTypeIndex">Engine shift class of the assignment: 0 early, 1 late, 2 night</param>
    public bool ForbidsClass(int shiftTypeIndex) => Keyword switch
    {
        ScheduleCommandKeyword.Free => true,
        ScheduleCommandKeyword.OnlyEarly => shiftTypeIndex != ShiftTypeInference.EarlyIndex,
        ScheduleCommandKeyword.NoEarly => shiftTypeIndex == ShiftTypeInference.EarlyIndex,
        ScheduleCommandKeyword.OnlyLate => shiftTypeIndex != ShiftTypeInference.LateIndex,
        ScheduleCommandKeyword.NoLate => shiftTypeIndex == ShiftTypeInference.LateIndex,
        ScheduleCommandKeyword.OnlyNight => shiftTypeIndex != ShiftTypeInference.NightIndex,
        ScheduleCommandKeyword.NoNight => shiftTypeIndex == ShiftTypeInference.NightIndex,
        _ => false,
    };

    /// <summary>Whether a shift of that kind breaks this keyword, judged on the kind's engine class.</summary>
    /// <param name="kind">Slot kind of the assignment; the day shift is judged as late</param>
    public bool ForbidsKind(AutofillShiftKind kind) => ForbidsClass(AutofillShiftCatalog.ShiftTypeIndexOf(kind));
}
