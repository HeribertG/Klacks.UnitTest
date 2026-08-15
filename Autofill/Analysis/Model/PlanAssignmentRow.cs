// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One in-period assignment, flat enough to survive a round trip through the metrics JSON. It exists
/// so a LATER run can be diffed against a FINISHED one slot by slot without re-running its engine:
/// every other metric in the artifact is an aggregate, and an aggregate cannot say which slot moved.
/// <para>
/// The field was added on 2026-08-14 for scenario 6, whose control run is the already finished
/// scenario-5 main run. Artifacts written before that day do not carry it, and a reader must say so
/// rather than silently diff against an empty list.
/// </para>
/// </summary>
/// <param name="Employee">Employee holding the slot</param>
/// <param name="Date">Calendar day the slot starts on</param>
/// <param name="SlotKind">Slot kind, which separates a day shift from a late one</param>
/// <param name="Order">Order the slot belongs to</param>
/// <param name="ShiftRefId">Shift reference of the slot — the only identifier that is unique per day</param>
public sealed record PlanAssignmentRow(
    string Employee,
    DateOnly Date,
    AutofillShiftKind SlotKind,
    int Order,
    Guid ShiftRefId);
