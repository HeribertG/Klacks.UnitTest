// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario5;

/// <summary>
/// Every value specification scenario 5 (tests/autofill/SPEC-SZENARIO5.md, owner-released 2026-08-14)
/// pins, as a named constant. Nothing here touches <see cref="AutofillSpecConstants"/> or
/// <c>Scenario4SpecConstants</c>: their shift counts, rank bounds and hour references describe other
/// rosters and are the definition of those scenarios' expectations, so the seventeen-employee
/// references live here and only here.
/// <para>
/// ARITHMETIC FRAMING. Three identically cut orders, each split into early, late, night AND a day
/// shift 08:00-16:00, over the 31 days of March 2026 demand 3 x 4 x 31 = 372 shifts = 2976 h, twelve
/// assignments every single day. Seventeen employees at 180 guaranteed hours offer 3060 h, so the
/// demand is 97.25 % of the supply.
/// </para>
/// <para>
/// RHYTHM. The roster offers 17 x 31 = 527 employee days and the plan needs 372 of them, a work share
/// of 70.6 %. A clean five-work/two-free rhythm is 5/7 = 71.43 %, so 5/2 is just barely reachable here
/// — unlike in scenario 4, where 60 % forced longer free blocks — and the free-block histogram is
/// expected to peak at 2.
/// </para>
/// <para>
/// TOP-DOWN, two resolutions, and the test MEASURES which one the algorithm picks instead of deciding
/// for it. (a) "Full means at least the guarantee": 180 h are 22.5 shifts, which no plan can hand out,
/// so a full guarantee costs 23 shifts (184 h) and 372 / 23 = 16.2 — sixteen ranks at 23 shifts, the
/// remaining 4 shifts for rank 17. (b) The flat approximation 372 / 17 = 21.9 shifts, about 175 h and
/// 97 % of the guarantee, for everybody. Neither of the two showing up is a finding of its own.
/// </para>
/// <para>
/// TWO COHORTS the scenario creates on purpose. The night pool is fifteen employees, not seventeen:
/// MA-16 and MA-17 are blacklisted from every night shift, so 93 night shifts spread over 15 people,
/// 6.2 each. And the day shifts are 93 while the two employees who prefer them can work at most about
/// 22 each under 5/2 — so at least 49 day shifts necessarily go to employees who never asked for one,
/// which is NOT a preference violation but the arithmetic of a soft preference.
/// </para>
/// </summary>
public static class Scenario5SpecConstants
{
    /// <summary>Number of parallel orders; each is cut into four shifts.</summary>
    public const int OrderCount = 3;

    /// <summary>Shifts one order demands per day: early, late, night and the day shift.</summary>
    public const int ShiftsPerOrderPerDay = 4;

    /// <summary>Employees in the roster; their position in the list is their rank.</summary>
    public const int EmployeeCount = 17;

    /// <summary>Demanded shifts: 3 orders x 4 shifts x 31 days.</summary>
    public const int TotalRequiredShifts = 372;

    /// <summary>Demanded hours behind <see cref="TotalRequiredShifts"/>.</summary>
    public const double TotalRequiredHours = 2976;

    /// <summary>Offered hours: 17 x 180 h.</summary>
    public const double OfferedHours = 3060;

    /// <summary>Assignments every single day of the period must carry: 3 orders x 4 shifts.</summary>
    public const int AssignmentsPerDay = 12;

    /// <summary>Employee days the roster offers over the period: 17 x 31.</summary>
    public const int TotalEmployeeDays = 527;

    /// <summary>Day shifts the period demands: 3 orders x 31 days.</summary>
    public const int TotalDayShifts = 93;

    /// <summary>Night shifts the period demands: 3 orders x 31 days.</summary>
    public const int TotalNightShifts = 93;

    /// <summary>Employees eligible for night duty; the two lowest ranks are blacklisted from it.</summary>
    public const int NightPoolSize = 15;

    /// <summary>Work share the plan needs: 372 of 527 employee days.</summary>
    public const double RequiredWorkShare = TotalRequiredShifts / (double)TotalEmployeeDays;

    /// <summary>Work share of a clean five-work/two-free rhythm: 5 of 7 days.</summary>
    public const double IdealFiveTwoWorkShare = 5.0 / 7.0;

    /// <summary>Free-block length the reachable 5/2 rhythm is expected to peak at.</summary>
    public const int ExpectedFreeBlockMode = 2;

    /// <summary>Top-down resolution (a): shifts a fully met 180 h guarantee costs, since 22.5 is unreachable.</summary>
    public const int FullGuaranteeShifts = 23;

    /// <summary>Top-down resolution (a): hours behind <see cref="FullGuaranteeShifts"/>.</summary>
    public const double FullGuaranteeHours = 184;

    /// <summary>Top-down resolution (a): ranks that can reach a full guarantee, 372 / 23 rounded down.</summary>
    public const int FullGuaranteeRanks = 16;

    /// <summary>Top-down resolution (a): shifts left for the last rank, 372 - 16 x 23.</summary>
    public const int RemainderRankShifts = 4;

    /// <summary>Top-down resolution (b): flat share per rank, 372 / 17, rounded to the shift.</summary>
    public const int FlatShareShifts = 22;

    /// <summary>Top-down resolution (b): hours of the unrounded flat share 372 / 17 x 8 h.</summary>
    public const double FlatShareHours = TotalRequiredHours / EmployeeCount;

    /// <summary>A7 / S5-8: minimum share of package transitions that rotate forwards.</summary>
    public const double MinForwardRotationRate = 0.85;

    /// <summary>S5-10: largest tolerated difference between the highest and lowest per-class count.</summary>
    public const int MaxShiftKindSpread = 2;

    /// <summary>
    /// S4-6, inherited: highest tolerated median of the order changes per employee. Owner decision B2
    /// turned order loyalty from a measurement into a soft rule and made the former reference value the
    /// ceiling.
    /// </summary>
    public const int MaxOrderSwitchMedian = 2;

    /// <summary>S5-12: a run above this many seconds is reported as an informative finding.</summary>
    public const int RuntimeFindingSeconds = 60;

    /// <summary>Employees still inside a previous-month package on the first day of the period.</summary>
    public const int OpenCarryInCount = 11;

    /// <summary>Employees whose previous-month package is already closed when the period starts.</summary>
    public const int ClosedCarryInCount = 6;

    /// <summary>
    /// Slots of the first day that no carry-in covers, and which the engine therefore staffs freshly:
    /// exactly one, the day shift of the third order.
    /// </summary>
    public const int UncoveredSlotsOnFirstDay = 1;

    /// <summary>First day the replanning run S5c plans freshly; everything before it is frozen.</summary>
    public static readonly DateOnly ReplanFrom = new(2026, 3, 16);

    /// <summary>
    /// Employee ids, zero padded so an ordinal sort keeps them in list order. Padding is essential:
    /// unpadded, "MA-10" sorts before "MA-2" and every artifact ordered by employee id would list the
    /// second half of the roster inside the first.
    /// </summary>
    public static readonly IReadOnlyList<string> Employees =
    [
        "MA-01", "MA-02", "MA-03", "MA-04", "MA-05",
        "MA-06", "MA-07", "MA-08", "MA-09", "MA-10",
        "MA-11", "MA-12", "MA-13", "MA-14", "MA-15",
        "MA-16", "MA-17",
    ];

    /// <summary>
    /// The employees scenario 5 adds on top of the scenario-4 roster: the two who prefer day shifts
    /// and are blacklisted from night duty.
    /// </summary>
    public static readonly IReadOnlyList<string> DayShiftEmployees = ["MA-16", "MA-17"];

    /// <summary>The employees scenario 5 inherits unchanged from scenario 4.</summary>
    public static readonly IReadOnlyList<string> InheritedEmployees =
    [
        "MA-01", "MA-02", "MA-03", "MA-04", "MA-05",
        "MA-06", "MA-07", "MA-08", "MA-09", "MA-10",
        "MA-11", "MA-12", "MA-13", "MA-14", "MA-15",
    ];

    /// <summary>
    /// Slots that fall free on each of the first days of the period as the open packages finish, in
    /// day order starting with the second day. Eleven reoccupations in total, the last of them on
    /// 5 March; the chain is pure fixture arithmetic and the guard recomputes it before every run.
    /// </summary>
    public static readonly IReadOnlyList<(int DayOffset, int FreedSlots)> FreedSlotChain =
    [
        (1, 3),
        (2, 3),
        (3, 3),
        (4, 2),
    ];

    /// <summary>Last day the freed-slot chain is checked against; nothing may fall free after it.</summary>
    public const int FreedSlotChainLastDayOffset = 5;

    /// <summary>
    /// The day MA-16's next package can start: his carried-in day-shift package ends on 3 March and the
    /// two contractual rest days follow. The specification names this date as the anchor scenario 6
    /// builds on, so the guard derives it and pins it rather than leaving it implicit.
    /// </summary>
    public static readonly DateOnly Ma16NextPackageStart = new(2026, 3, 6);

    /// <summary>Artifact folder of every scenario 5 run.</summary>
    public const string ScenarioName = "scenario5";

    /// <summary>Artifact file name of the main run.</summary>
    public const string RunAArtifactName = "Scenario5a";

    /// <summary>Artifact file name of the control run without preferences and commands.</summary>
    public const string RunBArtifactName = "Scenario5b";

    /// <summary>Artifact file name of the frozen-prefix replanning run.</summary>
    public const string RunCArtifactName = "Scenario5c";
}
