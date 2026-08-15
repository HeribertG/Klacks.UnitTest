// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>One assignment the plan placed inside a schedule-command window.</summary>
/// <param name="Date">Calendar day of the assignment</param>
/// <param name="Order">Order the shift belongs to</param>
/// <param name="SlotKind">Slot kind of the shift</param>
/// <param name="ShiftClass">Engine shift class of the shift; the keyword is judged against this</param>
/// <param name="ViolatesKeyword">True when this assignment breaks the window's keyword</param>
public sealed record ScheduleCommandWindowAssignment(
    DateOnly Date,
    int Order,
    AutofillShiftKind SlotKind,
    AutofillShiftKind ShiftClass,
    bool ViolatesKeyword);
