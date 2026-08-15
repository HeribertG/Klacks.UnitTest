// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// A work package that ran past the contractual maximum length. It is one of the two shapes the
/// arithmetic surplus of a plan can take — the other is a shortened free block — and the two together
/// are where the capacity a scenario lacks becomes visible in the finished plan rather than only in
/// the fixture.
/// </summary>
/// <param name="Employee">Employee identifier</param>
/// <param name="StartDate">First day of the package; may lie in the carry-in month</param>
/// <param name="EndDate">Last day of the package</param>
/// <param name="LengthDays">Length the package reached</param>
/// <param name="DaysOverMaximum">Days beyond the contractual maximum</param>
/// <param name="TouchesAbsenceWindow">
/// True when the package runs into, out of or through an absence window of any employee, i.e. when it
/// sits where the absence removed capacity. False for every scenario without absences
/// </param>
public sealed record PackageExtension(
    string Employee,
    DateOnly StartDate,
    DateOnly EndDate,
    int LengthDays,
    int DaysOverMaximum,
    bool TouchesAbsenceWindow);
