// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A work package that an absence cut short or a package the plan could not extend because an absence
/// begins the next day. The cut is FORCED by construction — the absence is a hard block and the
/// package could not have run on — which is exactly why it must be declared: a shortened package with
/// a stated cause is a different finding from one the algorithm shortened on its own.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="PackageStart">First day of the package</param>
/// <param name="PackageEnd">Last day of the package</param>
/// <param name="LengthDays">Length the package reached</param>
/// <param name="ShiftType">Shift class of the package's first day</param>
/// <param name="CutAt">The absence day that ends the package, i.e. the day after its last one</param>
/// <param name="MissingDays">Days the package falls short of the contractual maximum length</param>
/// <param name="Forced">Always true; an absence day cannot be worked, so no other plan was available</param>
/// <param name="Cause">Cause label carried into the report; always the absence cause</param>
public sealed record AbsenceCut(
    string Employee,
    DateOnly PackageStart,
    DateOnly PackageEnd,
    int LengthDays,
    AutofillShiftKind ShiftType,
    DateOnly CutAt,
    int MissingDays,
    bool Forced,
    string Cause);
