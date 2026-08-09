// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>Coverage of one order on its own, so no order can hide behind the total.</summary>
/// <param name="Order">Order index</param>
/// <param name="RequiredShifts">Sum of the required assignments over the slots of this order</param>
/// <param name="FilledShifts">Slots of this order that received an employee</param>
/// <param name="Unfilled">Slots of this order nobody received</param>
public sealed record OrderCoverage(
    int Order,
    int RequiredShifts,
    int FilledShifts,
    IReadOnlyList<UnfilledShift> Unfilled);
