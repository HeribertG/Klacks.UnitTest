// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One assignment that stands inside a schedule-command window and breaks its keyword: a shift on a
/// FREE day, or a shift of the wrong class under an Only* or No* command.
/// <para>
/// Measured by an independent scan of the finished plan against the fixture's command windows, not
/// read off the engine. The engine does compute a keyword violation, but only as a stage-0 SCORE:
/// nothing in the repository rejects a plan for having one, so an engine-sourced measurement would
/// report what the fitness disliked rather than what the plan contains.
/// </para>
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day of the assignment</param>
/// <param name="Keyword">Keyword in force on that employee and day</param>
/// <param name="AssignedClass">Engine shift class of the assignment: early, late or night</param>
/// <param name="AssignedSlotKind">Slot kind of the assignment; a day shift is visible here and not in the class</param>
/// <param name="Order">Order the shift belongs to</param>
/// <param name="IsCarryIn">
/// True when the offending shift is fixed previous-month input. Such a day is a FIXTURE
/// contradiction — the command and the carry-in cannot both be honoured — and the engine could not
/// remove it even if it wanted to, because a locked assignment is exempt from stage 0
/// </param>
public sealed record ScheduleCommandViolation(
    string Employee,
    DateOnly Date,
    ScheduleCommandKeyword Keyword,
    AutofillShiftKind AssignedClass,
    AutofillShiftKind AssignedSlotKind,
    int Order,
    bool IsCarryIn);
