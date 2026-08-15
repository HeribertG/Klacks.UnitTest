// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// The calendar day on which the fewest employees are available for the assignments it demands — the
/// day a scenario stands or falls on. Availability here is the INPUT-side count: everybody the roster
/// holds, minus everybody an absence or a FREE command closes that day. It says nothing about rhythm
/// rules, which can only tighten it further.
/// </summary>
/// <param name="Date">The day</param>
/// <param name="AvailableEmployees">Employees not closed by an absence or a FREE command</param>
/// <param name="RequiredAssignments">Assignments the day demands over all orders</param>
/// <param name="Ratio">Required assignments divided by available employees; 1 means everybody works</param>
public sealed record TightestDay(
    DateOnly Date,
    int AvailableEmployees,
    int RequiredAssignments,
    double Ratio);
