// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>A demanded shift slot that no employee received.</summary>
/// <param name="Date">Calendar day of the slot; for a night shift the day it starts on</param>
/// <param name="ShiftType">Shift kind of the slot</param>
/// <param name="Order">
/// Order the slot belongs to; <see cref="AutofillShiftCatalog.SingleOrderIndex"/> in a scenario
/// without an order dimension. Without it a gap would not say WHICH of three simultaneous night
/// shifts stayed open
/// </param>
public sealed record UnfilledShift(
    DateOnly Date,
    AutofillShiftKind ShiftType,
    int Order = AutofillShiftCatalog.SingleOrderIndex);
