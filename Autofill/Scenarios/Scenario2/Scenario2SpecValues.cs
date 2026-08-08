// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// The carry-in fixture table of specification scenario 2, one named constant per cell. Everything
/// scenario 2 adds on top of scenario 1 is pinned here so a specification change lands in exactly one
/// place; every value that scenario 1 and 2 share stays in <see cref="AutofillSpecConstants"/>.
/// <para>
/// Specification table (February 2026, 28 days). MA-1 night from 27.02., 2 of 5 served, still inside
/// the package on 01.03. MA-2 late from 25.02., 4 of 5 served, still inside. MA-3 early on 28.02.,
/// 1 of 5 served, still inside. MA-4 early 23.-27.02., package closed, first free day 28.02., new
/// package from 02.03. rotating early to late. MA-5 late 24.-28.02., package closed, free 01.-02.03.,
/// new package from 03.03. rotating late to night.
/// </para>
/// </summary>
public static class Scenario2SpecValues
{
    public const string Ma1 = AutofillSpecConstants.EmployeeIdPrefix + "1";

    public const string Ma2 = AutofillSpecConstants.EmployeeIdPrefix + "2";

    public const string Ma3 = AutofillSpecConstants.EmployeeIdPrefix + "3";

    public const string Ma4 = AutofillSpecConstants.EmployeeIdPrefix + "4";

    public const string Ma5 = AutofillSpecConstants.EmployeeIdPrefix + "5";

    /// <summary>MA-1 works nights in the previous month.</summary>
    public const AutofillShiftKind Ma1Kind = AutofillShiftKind.Night;

    public const AutofillShiftKind Ma2Kind = AutofillShiftKind.Late;

    public const AutofillShiftKind Ma3Kind = AutofillShiftKind.Early;

    public const AutofillShiftKind Ma4Kind = AutofillShiftKind.Early;

    public const AutofillShiftKind Ma5Kind = AutofillShiftKind.Late;

    /// <summary>MA-1 is still inside its package, so its first in-period shift stays a night shift.</summary>
    public const AutofillShiftKind Ma1ExpectedFirstKind = AutofillShiftKind.Night;

    public const AutofillShiftKind Ma2ExpectedFirstKind = AutofillShiftKind.Late;

    public const AutofillShiftKind Ma3ExpectedFirstKind = AutofillShiftKind.Early;

    /// <summary>MA-4 closed an early package, so rule 5 rotates its next package to late.</summary>
    public const AutofillShiftKind Ma4ExpectedFirstKind = AutofillShiftKind.Late;

    /// <summary>MA-5 closed a late package, so rule 5 rotates its next package to night.</summary>
    public const AutofillShiftKind Ma5ExpectedFirstKind = AutofillShiftKind.Night;

    public static readonly DateOnly Ma1PackageStart = new(2026, 2, 27);

    public static readonly DateOnly Ma1PackageEnd = new(2026, 2, 28);

    public static readonly DateOnly Ma2PackageStart = new(2026, 2, 25);

    public static readonly DateOnly Ma2PackageEnd = new(2026, 2, 28);

    public static readonly DateOnly Ma3PackageStart = new(2026, 2, 28);

    public static readonly DateOnly Ma3PackageEnd = new(2026, 2, 28);

    public static readonly DateOnly Ma4PackageStart = new(2026, 2, 23);

    public static readonly DateOnly Ma4PackageEnd = new(2026, 2, 27);

    public static readonly DateOnly Ma5PackageStart = new(2026, 2, 24);

    public static readonly DateOnly Ma5PackageEnd = new(2026, 2, 28);

    /// <summary>A11: MA-1 owes three more night shifts to reach a package of five.</summary>
    public const int Ma1RemainingDays = 3;

    /// <summary>A11: MA-2 owes one more late shift.</summary>
    public const int Ma2RemainingDays = 1;

    /// <summary>A11: MA-3 owes four more early shifts.</summary>
    public const int Ma3RemainingDays = 4;

    /// <summary>A closed previous-month package continues into no in-period day.</summary>
    public const int ClosedPackageRemainingDays = 0;

    /// <summary>Employees still inside a package on the first day of the period: MA-1, MA-2, MA-3.</summary>
    public const int OpenCarryInCount = 3;

    /// <summary>Previous-month work days of the whole fixture: 2 + 4 + 1 + 5 + 5.</summary>
    public const int TotalCarryInDays = 17;

    /// <summary>Diagnosis only: the day the specification table expects MA-4's new package to start on.</summary>
    public static readonly DateOnly Ma4ExpectedNewPackageStart = new(2026, 3, 2);

    /// <summary>Diagnosis only: the day the specification table expects MA-5's new package to start on.</summary>
    public static readonly DateOnly Ma5ExpectedNewPackageStart = new(2026, 3, 3);
}
