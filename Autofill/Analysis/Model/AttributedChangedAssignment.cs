// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// One slot the two runs staff differently, together with the cause the attribution rule assigns it.
/// </summary>
/// <param name="Date">Calendar day of the slot</param>
/// <param name="ShiftClass">Engine shift class of the slot</param>
/// <param name="SlotKind">Slot kind of the slot; separates a day shift from a late one</param>
/// <param name="Order">Order the slot belongs to</param>
/// <param name="EmployeeTreatment">Employee the treated run gave it to; null when it stayed empty there</param>
/// <param name="EmployeeControl">Employee the control run gave it to; null when it stayed empty there</param>
/// <param name="AttributedTo">Cause the attribution rule assigns</param>
public sealed record AttributedChangedAssignment(
    DateOnly Date,
    AutofillShiftKind ShiftClass,
    AutofillShiftKind SlotKind,
    int Order,
    string? EmployeeTreatment,
    string? EmployeeControl,
    DiffAttribution AttributedTo);
