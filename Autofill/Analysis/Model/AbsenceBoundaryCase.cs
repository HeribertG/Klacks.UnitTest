// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// An assignment that overlaps an absence window with at least one of its two ends. It carries the
/// two answers the boundary question needs side by side: which end actually lies inside the window,
/// and whether the plan accepted the assignment.
/// <para>
/// Owner decision O1 of 2026-08-14: work and a full-day absence on the same calendar day exclude each
/// other however few hours reach into it, and symmetrically at both edges. The engine implements only
/// half of that — it tests the start date and nothing else — so an accepted case whose evaluation is
/// <see cref="AbsenceEdgeEvaluation.End"/> is the measured gap between rule and implementation, not a
/// measurement error.
/// </para>
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="Date">Calendar day the assignment starts on</param>
/// <param name="EndsAt">Absolute end of the assignment; for a night shift this lies on the following day</param>
/// <param name="SlotKind">Slot kind of the assignment</param>
/// <param name="Order">Order the slot belongs to</param>
/// <param name="WindowFrom">First day of the absence window it touches</param>
/// <param name="WindowUntil">Last day of that window</param>
/// <param name="EvaluatedAgainst">Which end of the assignment lies inside the window</param>
/// <param name="Accepted">True when the plan holds this assignment; false only for a rejected candidate</param>
/// <param name="ViolatesOverlapRule">
/// True when the owner's overlap rule forbids the assignment — that is, whenever any part of it
/// reaches into the window, whichever end it is
/// </param>
public sealed record AbsenceBoundaryCase(
    string Employee,
    DateOnly Date,
    DateTime EndsAt,
    AutofillShiftKind SlotKind,
    int Order,
    DateOnly WindowFrom,
    DateOnly WindowUntil,
    AbsenceEdgeEvaluation EvaluatedAgainst,
    bool Accepted,
    bool ViolatesOverlapRule);
