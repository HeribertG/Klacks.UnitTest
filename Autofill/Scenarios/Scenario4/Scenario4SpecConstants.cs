// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Every value specification scenario 4 (tests/autofill/SPEC-SZENARIO4.md) pins, as a named constant.
/// Nothing here touches <see cref="AutofillSpecConstants"/>: its top-rank bound and hour references
/// describe a roster of five and silently redefine the band pins of the other scenarios, so the
/// fifteen-employee references live here and only here.
/// <para>
/// Arithmetic framing. Three identically cut orders, each split into early, late and night, over the
/// 31 days of March 2026 demand 3 x 3 x 31 = 279 shifts = 2232 h. Fifteen employees at 180 guaranteed
/// hours offer 2700 h, an occupancy of 82.7 % — the same ratio scenario 1 has, which is what keeps the
/// two comparable. Nine of fifteen employees work on any given day, a work share of 60 %.
/// </para>
/// <para>
/// Two resolutions follow from that, and the test measures which one the algorithm picks instead of
/// deciding for it. RHYTHM resolution: a work share of 60 % means a cycle of 8.33 days, so five shifts
/// followed by three or four free days, and the free-block histogram peaks at 3 and 4. TOP-DOWN
/// resolution: 279 / 22 = 12.7, so ranks 1 to 12 run a clean 5/2 at 22 shifts each (264 shifts), rank
/// 13 takes the remaining 15 and ranks 14 and 15 receive nothing. Neither of the two showing up is a
/// finding of its own.
/// </para>
/// </summary>
public static class Scenario4SpecConstants
{
    /// <summary>Number of parallel orders; each is cut into the same three shifts.</summary>
    public const int OrderCount = 3;

    /// <summary>Employees in the roster; their position in the list is their rank.</summary>
    public const int EmployeeCount = 15;

    /// <summary>Demanded shifts: 3 orders x 3 shifts x 31 days.</summary>
    public const int TotalRequiredShifts = 279;

    /// <summary>Demanded hours behind <see cref="TotalRequiredShifts"/>.</summary>
    public const double TotalRequiredHours = 2232;

    /// <summary>Offered hours of the main run: 15 x 180 h.</summary>
    public const double OfferedHours = 2700;

    /// <summary>Assignments every single day of the period must carry: 3 orders x 3 shifts.</summary>
    public const int AssignmentsPerDay = 9;

    /// <summary>Employees at work on any given day; the remaining six are free.</summary>
    public const int EmployeesAtWorkPerDay = 9;

    /// <summary>Highest list rank the top-down reference of this scenario covers (1-based).</summary>
    public const int TopRankUpperBound = 12;

    /// <summary>Top-down resolution: shifts each of the ranks 1 to 12 is expected to reach.</summary>
    public const int ReferenceShiftsTopRanks = 22;

    /// <summary>Top-down resolution: hours behind <see cref="ReferenceShiftsTopRanks"/>.</summary>
    public const double ReferenceHoursTopRanks = 176;

    /// <summary>Top-down resolution: list rank that takes the remainder after the clean 5/2 ranks.</summary>
    public const int RemainderRank = 13;

    /// <summary>Top-down resolution: shifts left over for <see cref="RemainderRank"/>: 279 - 12 x 22.</summary>
    public const int ReferenceShiftsRemainderRank = 15;

    /// <summary>Top-down resolution: hours behind <see cref="ReferenceShiftsRemainderRank"/>.</summary>
    public const double ReferenceHoursRemainderRank = 120;

    /// <summary>Top-down resolution: the two lowest ranks are expected to receive nothing at all.</summary>
    public const int ReferenceZeroHourRanks = 2;

    /// <summary>Rhythm resolution: length of the work package the 60 % work share produces.</summary>
    public const int RhythmPackageLength = 5;

    /// <summary>Rhythm resolution: shortest free block the 8.33-day cycle produces.</summary>
    public const int RhythmFreeBlockLow = 3;

    /// <summary>Rhythm resolution: longest free block the 8.33-day cycle produces.</summary>
    public const int RhythmFreeBlockHigh = 4;

    /// <summary>Calibration run S4b: 15 x 150 h = 2250 h against 2232 h of demand.</summary>
    public const double CalibrationOfferedHours = 2250;

    /// <summary>S4-15: shortest free block the calibration run is expected to peak at.</summary>
    public const int CalibrationFreeBlockLow = 2;

    /// <summary>S4-15: longest free block the calibration run is expected to peak at.</summary>
    public const int CalibrationFreeBlockHigh = 3;

    /// <summary>S4-15: forward rotation rate the calibration run must reach.</summary>
    public const double CalibrationMinForwardRotationRate = 0.85;

    /// <summary>
    /// S4-15 (owner decision B6, 2026-08-10): floor on how many ranks must fully reach the guaranteed
    /// hours. Stage 1 of the fitness maximizes this count and dominates any move that would spread
    /// hours more evenly instead (proof: <c>Stage1GuaranteeDominanceTests</c>), so a "95 % for every
    /// rank" floor is arithmetically unreachable — it caps at 9 full guarantees while stage 1 holds
    /// more. 12 is the measured optimum on HEAD and replaces the abandoned "all fifteen at 95 %"
    /// requirement (`docs/PLAN-autofill-e4-e7-2026-08-09.md`, "S4-15 ist beweisbar unerreichbar").
    /// </summary>
    public const int MinimumFullyMetGuarantees = 12;

    /// <summary>
    /// S4-15 (owner decision B6, 2026-08-10): every rank that does not fully reach the guarantee must
    /// still hold at least this share of it. Measured on HEAD: ranks 13-15 at 136/144/128 h = 91/96/85 %
    /// of the 150 h guarantee, so 85 % is the tightest of the three and the floor this constant pins.
    /// </summary>
    public const double RemainingRankFloorShare = 0.85;

    /// <summary>
    /// S4-6: highest tolerated median of the order changes per employee. Owner decision B2 turned
    /// order loyalty from a measurement into a rule, so the former reference value is now the ceiling.
    /// </summary>
    public const int OrderSwitchMedianReference = 2;

    /// <summary>
    /// S4-5: packages that touch more than one order. Rule 10 allows none — inside one package the
    /// employee stays on the order he started on.
    /// </summary>
    public const int MaxPackagesTouchingSeveralOrders = 0;

    /// <summary>S4-12: largest tolerated difference between the mean list ranks of two orders.</summary>
    public const double MaxOrderMeanRankSpread = 3;

    /// <summary>S4-16: a run above this many seconds is reported as an informative finding.</summary>
    public const int RuntimeFindingSeconds = 60;

    /// <summary>First day the replanning run S4e plans freshly; everything before it is frozen.</summary>
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
    ];

    /// <summary>
    /// The two employees whose extended free days at the start of the period are FORCED rather than
    /// chosen: their previous-month package ended on 26 February, so the two contractual rest days
    /// make them available from 1 March, and on 1 March all nine slots are held by the nine open
    /// carry-in packages. Derived arithmetically by the fixture guard, pinned here as the expectation.
    /// </summary>
    public static readonly IReadOnlyList<string> ForcedIdleOnFirstDay = ["MA-14", "MA-15"];

    /// <summary>Employees still inside a previous-month package on the first day of the period.</summary>
    public const int OpenCarryInCount = 9;

    /// <summary>Employees whose previous-month package is already closed when the period starts.</summary>
    public const int ClosedCarryInCount = 6;

    /// <summary>
    /// Slots that fall free on each of the first days of the period as the open packages finish, in
    /// day order starting with the second day. Nine reoccupations in total, the last of them on
    /// 5 March; the chain is pure fixture arithmetic and the guard recomputes it before every run.
    /// </summary>
    public static readonly IReadOnlyList<(int DayOffset, int FreedSlots)> FreedSlotChain =
    [
        (1, 3),
        (2, 2),
        (3, 2),
        (4, 2),
    ];

    /// <summary>Last day the freed-slot chain is checked against; nothing may fall free after it.</summary>
    public const int FreedSlotChainLastDayOffset = 5;

    /// <summary>Artifact folder of every scenario 4 run.</summary>
    public const string ScenarioName = "scenario4";

    /// <summary>Artifact file name of the main run.</summary>
    public const string RunAArtifactName = "Scenario4a";

    /// <summary>Artifact file name of the calibration run at 150 guaranteed hours.</summary>
    public const string RunBArtifactName = "Scenario4b";

    /// <summary>Artifact file name of the symmetry run with the outer orders swapped.</summary>
    public const string RunCArtifactName = "Scenario4c";

    /// <summary>Artifact file name of the run without a previous month.</summary>
    public const string RunDArtifactName = "Scenario4d";

    /// <summary>Artifact file name of the frozen-prefix replanning run.</summary>
    public const string RunEArtifactName = "Scenario4e";

    /// <summary>Artifact file name of the timing probe.</summary>
    public const string TimingProbeArtifactName = "Scenario4TimingProbe";
}
