// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario5;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario6;

/// <summary>
/// Every value specification scenario 6 (tests/autofill/SPEC-SZENARIO6.md, owner-released 2026-08-14)
/// pins, as a named constant. Scenario 6 IS scenario 5 plus two weeks of holiday, so everything the
/// roster, the orders, the carry-in and the preferences state is read from
/// <see cref="Scenario5SpecConstants"/> and never restated here; only what the holiday adds lives in
/// this file.
/// <para>
/// THE CONTROL RUN IS NOT RUN. S6-K is the finished scenario-5 main run S5a. The specification says so
/// explicitly, and the reason is arithmetic: one run of this size measured 522 s in phase C, and eight
/// scenario-6 variants plus a control would be an hour of engine time spent reproducing a plan that
/// already exists as an artifact. The comparison therefore reads that artifact — see
/// <see cref="BaselineTestName"/> — and a missing artifact makes the comparison INCONCLUSIVE, never
/// green.
/// </para>
/// <para>
/// THE HOLIDAY VALUE IS A SIMULATED MACRO RESULT, owner decision O3. In the shipped system no absence
/// type carries a MacroId, so <c>BreakMacroService</c> never runs and <c>Break.WorkTime</c> is
/// whatever the booking wrote. Scenario 6 does not model that defect; it models the RESULT the macro
/// is supposed to produce, and states the reference semantics openly: credit on contractual working
/// days only, 180 h divided by the 22 working days of March 2026 = 8.1818... h. The two weekends
/// inside the window get rows with zero hours — blocked, unpaid — which is what makes the credit
/// 81.82 h and not 114.55 h.
/// </para>
/// <para>
/// THE NUMBERING S6-1 TO S6-20 is derived from the specification and not invented here. The spec
/// describes S6-2, S6-3, S6-5, S6-6 and S6-12 in prose and lists the remaining fifteen as one
/// enumeration for the ids "S6-1/4/7-11/13-20". That enumeration holds exactly fifteen items, and
/// they land on the fifteen ids in the order written: 1 absolute, 4 not free days, 7 densification,
/// 8 tightest point, 9 preference stays preference, 10 redundancy, 11 February untouched, 13 the
/// keyword window that became pointless, 14 full coverage without both preference employees, 15 no
/// hard rule sacrificed, 16 five-day packages, 17 free-block mode, 18 rank fulfilment, 19 spread,
/// 20 determinism.
/// </para>
/// </summary>
public static class Scenario6SpecConstants
{
    /// <summary>Artifact folder of every scenario 6 run.</summary>
    public const string ScenarioName = "scenario6";

    /// <summary>Artifact folder the control run S6-K was written to; it is the scenario-5 folder.</summary>
    public const string BaselineScenarioName = Scenario5SpecConstants.ScenarioName;

    /// <summary>Artifact file name of the control run S6-K, which is the scenario-5 main run.</summary>
    public const string BaselineTestName = Scenario5SpecConstants.RunAArtifactName;

    /// <summary>Run label of the control run's artifact; the runner writes the first of its two runs as run1.</summary>
    public const string BaselineRunLabel = "run1";

    /// <summary>Name of the control run in the report.</summary>
    public const string BaselineLabel = "S6-K = S5a";

    /// <summary>Artifact file name of the main run.</summary>
    public const string RunAArtifactName = "Scenario6a";

    /// <summary>Artifact file name of the calibration run with an eighteenth employee.</summary>
    public const string RunBArtifactName = "Scenario6b";

    /// <summary>Artifact file name of the run whose absence sits on the top rank.</summary>
    public const string RunCArtifactName = "Scenario6c";

    /// <summary>Artifact file name of the run whose absence crosses the month boundary.</summary>
    public const string RunDArtifactName = "Scenario6d";

    /// <summary>Artifact file name of the run whose absence covers a keyword window.</summary>
    public const string RunEArtifactName = "Scenario6e";

    /// <summary>Artifact file name of the run in which both preference employees are absent.</summary>
    public const string RunFArtifactName = "Scenario6f";

    /// <summary>Artifact file name of the provably unsolvable run.</summary>
    public const string RunGArtifactName = "Scenario6g";

    /// <summary>Artifact file name of the replanning run.</summary>
    public const string RunHArtifactName = "Scenario6h";

    /// <summary>The absence name every holiday row carries; it reaches the engine as the blocker reason.</summary>
    public const string HolidayReason = "Ferien";

    /// <summary>First day of the holiday of the main run; a Monday.</summary>
    public static readonly DateOnly HolidayFrom = new(2026, 3, 9);

    /// <summary>Last day of the holiday of the main run; a Sunday, so the window is exactly two calendar weeks.</summary>
    public static readonly DateOnly HolidayUntil = new(2026, 3, 22);

    /// <summary>Calendar days the holiday window spans: two full weeks.</summary>
    public const int HolidayDays = 14;

    /// <summary>Rows the holiday window produces; one per calendar day, owner decision O2.</summary>
    public const int HolidayRows = HolidayDays;

    /// <summary>Rows of the window that carry hours: the ten Mondays to Fridays of two weeks.</summary>
    public const int HolidayPaidRows = 10;

    /// <summary>Rows of the window that carry no hours: the four weekend days of two weeks.</summary>
    public const int HolidayUnpaidRows = HolidayDays - HolidayPaidRows;

    /// <summary>Contractual working days of March 2026 — the divisor of the daily reference value.</summary>
    public const int MarchWorkingDays = 22;

    /// <summary>
    /// Hours one absent working day credits: the monthly guarantee divided by the month's working
    /// days. Constructed as a decimal division and never as a rounded literal, because 8.1818 also
    /// sums to within a hundredth of the expected credit and would pass a sum check while being a
    /// different number.
    /// </summary>
    public static readonly decimal DailyCreditHours =
        (decimal)AutofillSpecConstants.GuaranteedHours / MarchWorkingDays;

    /// <summary>Hours the whole window credits: ten paid days at the daily reference value.</summary>
    public static readonly decimal WindowCreditHours = HolidayPaidRows * DailyCreditHours;

    /// <summary>Tolerance of the credit sum in the guard, in hours.</summary>
    public const double CreditTolerance = 0.01;

    /// <summary>The employee the main run sends on holiday: list rank 16.</summary>
    public const string HolidayEmployee = "MA-16";

    /// <summary>The employee whose holiday run S6c books: list rank 1, the top of the top-down order.</summary>
    public const string TopRankHolidayEmployee = "MA-01";

    /// <summary>The employee whose holiday run S6d books across the month boundary.</summary>
    public const string CrossMonthHolidayEmployee = "MA-04";

    /// <summary>The employee whose holiday run S6e books over his own keyword window.</summary>
    public const string KeywordHolidayEmployee = "MA-03";

    /// <summary>The second preference employee, absent in run S6f alongside MA-16.</summary>
    public const string SecondPreferenceEmployee = "MA-17";

    /// <summary>
    /// The six employees run S6g sends on holiday at once. SETTING, not specification: the owner text
    /// asks for six employees without keywords or preferences and names none, so the fixture picks the
    /// six consecutive ranks that carry neither and says so.
    /// </summary>
    public static readonly IReadOnlyList<string> UnsolvableRunEmployees =
        ["MA-06", "MA-07", "MA-08", "MA-09", "MA-10", "MA-11"];

    /// <summary>First day of the cross-month holiday of run S6d; it lies in February and the engine never sees it.</summary>
    public static readonly DateOnly CrossMonthHolidayFrom = new(2026, 2, 23);

    /// <summary>Last day of the cross-month holiday of run S6d.</summary>
    public static readonly DateOnly CrossMonthHolidayUntil = new(2026, 3, 8);

    /// <summary>First day of the cross-month holiday the fixture can express: the first day of the period.</summary>
    public static readonly DateOnly CrossMonthModelledFrom = AutofillSpecConstants.PeriodFrom;

    /// <summary>First day of the holiday of run S6e, which covers the OnlyEarly window of MA-03 entirely.</summary>
    public static readonly DateOnly KeywordHolidayFrom = new(2026, 3, 16);

    /// <summary>Last day of the holiday of run S6e.</summary>
    public static readonly DateOnly KeywordHolidayUntil = new(2026, 3, 29);

    /// <summary>The keyword window of MA-03 that run S6e covers completely.</summary>
    public static readonly DateOnly KeywordWindowFrom = new(2026, 3, 18);

    /// <summary>Last day of that keyword window.</summary>
    public static readonly DateOnly KeywordWindowUntil = new(2026, 3, 24);

    /// <summary>The FREE window of MA-01 that run S6c covers completely.</summary>
    public static readonly DateOnly FreeWindowFrom = new(2026, 3, 10);

    /// <summary>Last day of that FREE window.</summary>
    public static readonly DateOnly FreeWindowUntil = new(2026, 3, 12);

    /// <summary>Identifier of the eighteenth employee run S6b adds.</summary>
    public const string EighteenthEmployee = "MA-18";

    /// <summary>Guaranteed hours of the eighteenth employee; the specification lowers them.</summary>
    public const double EighteenthEmployeeGuaranteedHours = 165;

    /// <summary>Employees run S6b plans with.</summary>
    public const int CalibrationEmployeeCount = Scenario5SpecConstants.EmployeeCount + 1;

    /// <summary>First day run S6h replans freshly; everything before it is frozen.</summary>
    public static readonly DateOnly ReplanFrom = Scenario5SpecConstants.ReplanFrom;

    /// <summary>
    /// Employee days the main run offers: sixteen employees over the 31 days of March plus the
    /// seventeenth's 17 remaining ones. The specification's densification band is derived from exactly
    /// this capacity, so it is the key that decides which runs the band applies to — the calibration
    /// run has more places and the runs with several absentees have fewer, and both would be judged
    /// against an arithmetic that is not theirs.
    /// </summary>
    public const int MainRunSlotsTotal = 513;

    /// <summary>S6-7: extra shifts the arithmetic of the main run forces beyond a clean 5/2 rhythm.</summary>
    public const int ExpectedForcedExtraShifts = 6;

    /// <summary>S6-7: tolerance around <see cref="ExpectedForcedExtraShifts"/>, in shifts.</summary>
    public const int ForcedExtraShiftsTolerance = 3;

    /// <summary>S6-8: the tightest day of the main run must still leave a solution, so its ratio stays at most this.</summary>
    public const double MaxTightestDayRatio = 1.0;

    /// <summary>Day shifts the holiday window demands: three orders over fourteen days.</summary>
    public const int DayShiftsInWindow = Scenario5SpecConstants.OrderCount * HolidayDays;

    /// <summary>
    /// S6-9: day shifts inside the window that necessarily go to employees who never asked for one.
    /// The one remaining preference employee can work about ten of the fourteen days under 5/2, so at
    /// least this many of the 42 land elsewhere — arithmetic, not a preference violation.
    /// </summary>
    public const int MinDayShiftsToNonPreferring = 32;

    /// <summary>S6-16: share of packages of five days the calibration run must reach.</summary>
    public const double CalibrationFiveDayPackageShare = 0.70;

    /// <summary>S6-18: fulfilment every rank of the calibration run except the absent one must reach.</summary>
    public const double CalibrationFulfilmentThreshold = 0.95;

    /// <summary>S6-19: largest tolerated difference between the highest and lowest per-class count in S6b.</summary>
    public const int CalibrationMaxShiftKindSpread = Scenario5SpecConstants.MaxShiftKindSpread;

    /// <summary>S6-17: free-block length the calibration run's histogram must peak at.</summary>
    public const int CalibrationFreeBlockMode = Scenario5SpecConstants.ExpectedFreeBlockMode;

    /// <summary>
    /// How many day shifts inside the absence windows must necessarily go to employees who never asked
    /// for one: all of them, minus what the preferring employees still available can work under the
    /// contractual five-work/two-free rhythm.
    /// <para>
    /// It is DERIVED and not stated, so it holds for every variant of the scenario. Both figures the
    /// specification names fall out of it: 32 when one preference employee remains and 42 when neither
    /// does. A literal would have been right for the main run and silently wrong for every other.
    /// </para>
    /// </summary>
    /// <param name="definition">Scenario to derive the floor for; supplies roster and absences</param>
    /// <param name="windowDays">Calendar days the absence windows cover in total</param>
    public static int DayShiftsThatMustSpillOver(AutofillScenarioDefinition definition, int windowDays)
    {
        var available = Scenario5SpecConstants.DayShiftEmployees
            .Count(e => definition.EmployeesInListOrder.Contains(e, StringComparer.Ordinal)
                        && definition.Absences.DaysOf(e).Count == 0);
        var cycle = AutofillSpecConstants.MaxWorkDays + AutofillSpecConstants.MinRestDays;
        var perHead = (int)Math.Ceiling(windowDays * AutofillSpecConstants.MaxWorkDays / (double)cycle);
        var total = Scenario5SpecConstants.OrderCount * windowDays;
        return Math.Max(0, total - (available * perHead));
    }
}
