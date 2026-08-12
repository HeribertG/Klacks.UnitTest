// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// Every value the autofill specification pins, as a named constant. Scenario tests must read their
/// expectations from here instead of repeating literals, so a specification change has exactly one
/// place to land.
/// <para>
/// Arithmetic framing of scenario 1 (kept here because the assertions only make sense against it):
/// 31 days x 3 shifts = 93 shifts = 744 h of demand. Five employees at 180 guaranteed hours offer
/// 900 h, so 180 h equal 22.5 shifts and the five together want 112.5 shifts while only 93 exist —
/// a structural undersupply of 19.5 shifts (156 h). A pure 5-work/2-free rhythm yields about 22
/// shifts per employee, i.e. 110 shifts in total, which is 17 more than the demand: a clean 5/2
/// pattern for all five employees at once is arithmetically impossible. The top-down rule
/// (specification rule 6) therefore predicts ranks 1-4 near 176 h (22 shifts in clean 5/2 packages)
/// and rank 5 near 40 h (5 shifts).
/// </para>
/// <para>
/// Calibration variant 1b lowers the guaranteed hours to 150 h (18.75 shifts, 750 h of demand for
/// 744 h of supply), which makes all goals satisfiable at the same time and separates genuine
/// algorithm defects from unavoidable goal conflicts.
/// </para>
/// </summary>
public static class AutofillSpecConstants
{
    /// <summary>First day of the planning period.</summary>
    public static readonly DateOnly PeriodFrom = new(2026, 3, 1);

    /// <summary>Last day of the planning period, inclusive.</summary>
    public static readonly DateOnly PeriodUntil = new(2026, 3, 31);

    /// <summary>First day of the carry-in month used by scenario 2.</summary>
    public static readonly DateOnly CarryInMonthFrom = new(2026, 2, 1);

    /// <summary>Last day of the carry-in month, i.e. the day before the period starts.</summary>
    public static readonly DateOnly CarryInMonthUntil = new(2026, 2, 28);

    public const int PeriodDays = 31;

    public const string EarlyStartTime = "07:00";

    public const string EarlyEndTime = "15:00";

    public const string LateStartTime = "15:00";

    public const string LateEndTime = "23:00";

    public const string NightStartTime = "23:00";

    /// <summary>The night shift ends on the following calendar day; the shift's date stays the start day.</summary>
    public const string NightEndTime = "07:00";

    public const double ShiftHours = 8;

    public const int ShiftsPerDay = 3;

    /// <summary>31 days x 3 shifts.</summary>
    public const int TotalRequiredShifts = 93;

    /// <summary>93 shifts x 8 h.</summary>
    public const double TotalRequiredHours = 744;

    public const int EmployeeCount = 5;

    /// <summary>Guaranteed hours of scenario 1 and 2 — deliberately above the available demand.</summary>
    public const double GuaranteedHours = 180;

    /// <summary>Guaranteed hours of calibration variant 1b — demand and supply almost match.</summary>
    public const double CalibrationGuaranteedHours = 150;

    /// <summary>Total offered hours in scenario 1: 5 x 180 h.</summary>
    public const double OfferedHours = 900;

    /// <summary>Structural undersupply of scenario 1 in shifts: 112.5 wanted minus 93 available.</summary>
    public const double StructuralUndersupplyShifts = 19.5;

    /// <summary>Structural undersupply of scenario 1 in hours.</summary>
    public const double StructuralUndersupplyHours = 156;

    /// <summary>Contractual soft cap on the length of one work package, in days.</summary>
    public const int MaxWorkDays = 5;

    /// <summary>Contractual number of free days required between two work packages.</summary>
    public const int MinRestDays = 2;

    /// <summary>Contractual rest time between two shifts, in hours.</summary>
    public const double MinRestHours = 11;

    /// <summary>Hard upper bound on consecutive work days; deliberately one above <see cref="MaxWorkDays"/>.</summary>
    public const int MaxConsecutiveDays = 6;

    public const double MaxDailyHours = 10;

    /// <summary>
    /// Weekly hours cap. Generous against the 5 x 8 h = 40 h rhythm and identical to the production
    /// default. Wizard 1 never enforces it as a constraint, but the fuzzy bidding agent divides the
    /// hours assigned so far by this value to derive its WeeklyLoad feature, so raising it would
    /// silently change the seeding behaviour instead of relaxing a limit.
    /// </summary>
    public const double MaxWeeklyHours = 50;

    public const double MaxOptimalGap = 2;

    /// <summary>Employees start the period with an empty hour account.</summary>
    public const double CurrentHours = 0;

    /// <summary>
    /// Neutral motivation for every employee. Identical values keep the fuzzy bidding agent from
    /// preferring one employee over another for a reason the specification does not mention.
    /// </summary>
    public const double Motivation = 0.5;

    /// <summary>
    /// All surcharge rates are zero, so planned hours equal shift hours exactly. The reference
    /// expectation "22 shifts = 176 h" of rule 6 only holds while no surcharge inflates the hour
    /// account the fitness compares against the guaranteed hours.
    /// </summary>
    public const decimal SurchargeRate = 0;

    /// <summary>Population size of the GA run — mirrors the proven integration-test configuration.</summary>
    public const int PopulationSize = 20;

    /// <summary>Generation cap of the GA run — mirrors the proven integration-test configuration.</summary>
    public const int MaxGenerations = 600;

    /// <summary>
    /// Equal to <see cref="MaxGenerations"/>, which disables early stopping. A truncated run would
    /// fail the hour-fulfilment assertions for budget reasons rather than algorithmic ones.
    /// </summary>
    public const int EarlyStopNoImprovementGenerations = 600;

    /// <summary>Fixed seed; combined with sequential evaluation and no wall-clock budget it makes runs reproducible.</summary>
    public const int RandomSeed = 42;

    /// <summary>Sequential evaluation — the reference mode for determinism proofs.</summary>
    public const int EvaluationParallelism = 1;

    /// <summary>A5: the package-length histogram must peak at this length.</summary>
    public const int ExpectedPackageLengthMode = 5;

    /// <summary>A5: no package may exceed this length.</summary>
    public const int MaxAllowedPackageLength = 5;

    /// <summary>A5: share of packages of length 1 or 2 that is still acceptable.</summary>
    public const double ShortPackageShareLimit = 0.15;

    /// <summary>A5: tolerance on <see cref="ShortPackageShareLimit"/>, in percentage points.</summary>
    public const double ShortPackageShareTolerance = 0.05;

    /// <summary>A5: a package counts as short when its length is at most this many days.</summary>
    public const int ShortPackageMaxLength = 2;

    /// <summary>A6: minimum fulfilment of the reference hours for the ranks the rule applies to.</summary>
    public const double FulfilmentThreshold = 0.95;

    /// <summary>A6 reference: hours ranks 1-4 are expected to reach in scenario 1.</summary>
    public const double ReferenceHoursTopRanks = 176;

    /// <summary>A6 reference: shifts behind <see cref="ReferenceHoursTopRanks"/>.</summary>
    public const int ReferenceShiftsTopRanks = 22;

    /// <summary>A6 reference: hours the last rank is expected to be left with in scenario 1.</summary>
    public const double ReferenceHoursLastRank = 40;

    /// <summary>A6 reference: shifts behind <see cref="ReferenceHoursLastRank"/>.</summary>
    public const int ReferenceShiftsLastRank = 5;

    /// <summary>A6: highest list rank still covered by the top-down reference expectation (1-based).</summary>
    public const int TopRankUpperBound = 4;

    /// <summary>A7: minimum share of package transitions that follow early to late to night to early.</summary>
    public const double MinForwardRotationRate = 0.80;

    /// <summary>A8: largest tolerated difference between the highest and lowest per-shift-kind count.</summary>
    public const int MaxShiftKindSpread = 2;

    /// <summary>One configured rest day between two work blocks equals 24 hours (owner ruling 2026-08-12).</summary>
    public const int HoursPerRestDay = 24;

    /// <summary>
    /// A12: rest required after a carried-in package has been completed — the configured rest days
    /// between two work blocks computed as HOURS from shift end to shift start (owner ruling
    /// 2026-08-12, SPEC.md decision 12d: "2 days, computed as hours, so 48h", not two calendar days).
    /// </summary>
    public const double MinRestHoursAfterCarryIn = MinRestDays * HoursPerRestDay;

    /// <summary>Comparison epsilon for the monotonicity check on fulfilment percentages.</summary>
    public const double MonotonicityEpsilon = 1e-9;

    /// <summary>Prefix of the generated employee identifiers; the number behind it is the list rank.</summary>
    public const string EmployeeIdPrefix = "MA-";

    /// <summary>Date format of <c>CoreShift.Date</c>; the engine sorts slots by this string ordinally.</summary>
    public const string IsoDateFormat = "yyyy-MM-dd";

    public const string EarlyShiftName = "Frueh";

    public const string LateShiftName = "Spaet";

    public const string NightShiftName = "Nacht";
}
