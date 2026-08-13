// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// Specification scenario 3, engine level — the month transition of scenario 2 plus the NACHT-BEF
/// restriction, resolved to the 73-triple ban list of owner decision S3-1. Three runs share one
/// <c>[OneTimeSetUp]</c>: L0 (scenario 2 unchanged, the control group), L1 (the treatment, judged by
/// every assertion of this class) and L5 (MA-2 unbounded, isolating the expiry effect); each run is
/// executed twice by the deterministic runner, which is what A26 reads. The general carry-in
/// assertions are inherited from <see cref="Scenario2AssertionsBase"/> and judge L1, exactly as the
/// specification inherits them onto the main run.
/// <para>
/// What these runs can and cannot prove: the engine knows only the bare ban set, so L1 exercises the
/// POOL behaviour under the restriction and presupposes the expiry semantics of MA-2's keyword
/// (findings K3/K4). A20/A21/A23 and the expiry proof itself run on API level against
/// EligibilityMatrixBuilder and are deliberately absent here; A22 runs in
/// <see cref="Scenario3UnstaffableNightTests"/> on the L4 fixture.
/// </para>
/// <para>
/// Expectations come from the specification. Where the engine cannot meet one, the assertion pins the
/// measurement of 2026-08-12 and names the unchanged target in its message (SPEC.md decision 11) —
/// A17, A19 and A25 here, A6, A12 and A13 in the base class — instead of staying permanently red,
/// which guarded nothing. A4, A5, A7 and A8 were removed entirely; their measurements are pinned by
/// the <c>Baseline_</c> guards.
/// </para>
/// <para>
/// The inherited <c>Baseline_</c> guards read <see cref="Scenario3BaselineValues"/>, which holds real
/// measurements since 2026-08-12; before that they were inert placeholders that asserted nothing.
/// Because no seed band is measured for this family — that would add six evolution runs per suite
/// execution — those pins are single-run values on seed 42 and therefore sharper than the band edges
/// of the other scenarios.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
public class Scenario3MainRunTests : Scenario2AssertionsBase
{
    /// <summary>Separator the analyzer uses when one slot holds several assignees.</summary>
    private const char AssigneeJoinSeparator = '+';

    private const double ShareEpsilon = 1e-9;

    /// <summary>
    /// A17, pinned measurement: rotation deviations that carry no provable reason. The 16 unproven
    /// deviations of 2026-08-12 dissolved under the decision-12b measurement rework of 2026-08-13 —
    /// every L1 package pair is a free restart across enough rest, the rotation-bound set is empty
    /// and the pin returned to the specification target as decision 6 demands. The telemetry gap of
    /// finding K8b only matters again once rotation-bound transitions reappear.
    /// </summary>
    private const int MaxUnexplainedRotationDeviations = 0;

    /// <summary>
    /// A17, pinned measurement 2026-08-12 (SPEC.md decision 11): rotation deviations of MA-3 and MA-4
    /// that do NOT carry the keyword reason. The three Early-to-Early boundaries per employee of
    /// 2026-08-12 were free restarts across enough rest and dissolved under the decision-12b
    /// measurement rework of 2026-08-13 — repeating the early kind after enough rest is lawful, so
    /// the pin returned to the specification target as decision 6 demands.
    /// </summary>
    private const int MaxUnprovenDeviationsPerNightBannedEmployee = 0;

    /// <summary>
    /// A6, pinned measurement 2026-08-12 evening (SPEC.md decision 13): under the unescalatable
    /// hour-based rest the hours level across the roster and rank 5 (152 h) sits 16 h — two shift
    /// lengths — above rank 4 (136 h). This override widens the A6 tolerance band for this scenario
    /// only; the one-shift-length band of the base stays the specification reading (rule 5), and
    /// winning the order back belongs to the commissioned package-aware repair stage.
    /// </summary>
    private const double PinnedTopDownToleranceHours = 2 * AutofillSpecConstants.ShiftHours;

    /// <inheritdoc />
    protected override double TopDownToleranceHours => PinnedTopDownToleranceHours;

    /// <summary>
    /// A19, pinned measurement 2026-08-12 evening (SPEC.md decision 13): shortened night packages
    /// beyond the one the pool arithmetic forces. The specification target is 0 and stays binding,
    /// coupled to the A5 short-package share. Re-pinned from 3 to 6 on the decision-12 engine — the
    /// splintering price of the unescalatable hour-based rest; winning it back belongs to the
    /// commissioned package-aware repair stage. The forced bound itself stays sharp at
    /// <see cref="Scenario3SpecValues.MaxForcedShortenings"/> — it is provable.
    /// </summary>
    private const int MaxUnexplainedShortenings = 6;

    /// <summary>
    /// A25, pinned measurement 2026-08-12 (SPEC.md decision 11): largest absolute deviation of a cohort
    /// member's night share per eligible day from the cohort mean. Replaces the relative 20 % tolerance
    /// of <see cref="Scenario3SpecValues.CohortRelativeTolerance"/>, which stays the binding
    /// specification target. Measured 2026-08-12 on engine af5f0fa: MA-1 17 nights on 31 eligible days
    /// = 0.5484 (deviation 0.1973), MA-2 3 on 20 = 0.15 (deviation 0.2011), MA-5 11 on 31 = 0.3548,
    /// mean 0.3511 — so MA-2 sets this edge. The 20 % target would allow 0.0702.
    /// </summary>
    private const double MaxCohortDeviation = 0.2011;

    /// <summary>
    /// A25, pinned measurement 2026-08-12 (SPEC.md decision 11): the cohort spread of the night shares,
    /// previously only reported in the failure message and asserted by nothing. Measured 2026-08-12 on
    /// engine af5f0fa: 0.3984 (MA-1 at 0.5484 against MA-2 at 0.15). Pinning it makes the second half
    /// of A25 a guard instead of a comment.
    /// </summary>
    private const double MaxCohortSpreadNight = 0.3984;

    /// <summary>Label of the control run in every diagnosis line.</summary>
    private const string L0Label = "L0";

    /// <summary>Label of the treatment run — the run every rule assertion of this class judges.</summary>
    private const string L1Label = "L1";

    /// <summary>Label of the diagnostic run that leaves MA-2's night keyword unbounded.</summary>
    private const string L5Label = "L5";

    private AutofillScenarioDefinition? _l0Definition;

    private AutofillScenarioDefinition? _l1Definition;

    private AutofillScenarioDefinition? _l5Definition;

    private DeterministicRunResult? _l0Run;

    private DeterministicRunResult? _l1Run;

    private DeterministicRunResult? _l5Run;

    private PlanAssignmentDiff? _diffL0ToL1;

    private PlanAssignmentDiff? _diffL5ToL1;

    private IReadOnlyList<string> _fixtureProblems = [];

    private IReadOnlyList<string> _precheckProblems = [];

    protected override AutofillScenarioDefinition Definition
    {
        get
        {
            EnsureFixtureIsValid();
            return _l1Definition!;
        }
    }

    protected override DeterministicRunResult Run
    {
        get
        {
            EnsureFixtureIsValid();
            return _l1Run!;
        }
    }

    /// <summary>Inert placeholder pins; scenario 3 has no measured band yet.</summary>
    protected override AutofillBaseline Baseline => Scenario3BaselineValues.Baseline;

    private DeterministicRunResult ControlRun
    {
        get
        {
            EnsureFixtureIsValid();
            return _l0Run!;
        }
    }

    private DeterministicRunResult DiagnosticRun
    {
        get
        {
            EnsureFixtureIsValid();
            return _l5Run!;
        }
    }

    private AutofillScenarioDefinition DiagnosticDefinition
    {
        get
        {
            EnsureFixtureIsValid();
            return _l5Definition!;
        }
    }

    private PlanAssignmentDiff DiffL0ToL1
    {
        get
        {
            EnsureFixtureIsValid();
            return _diffL0ToL1!;
        }
    }

    private PlanAssignmentDiff DiffL5ToL1
    {
        get
        {
            EnsureFixtureIsValid();
            return _diffL5ToL1!;
        }
    }

    [OneTimeSetUp]
    public void BuildAndRunScenarioThree()
    {
        try
        {
            _l0Definition = Scenario3EligibilityFixture.BuildL0();
            _l1Definition = Scenario3EligibilityFixture.BuildL1();
            _l5Definition = Scenario3EligibilityFixture.BuildL5();
        }
        catch (Exception exception)
        {
            _fixtureProblems = [$"scenario 3 could not be assembled: {exception.Message}"];
            return;
        }

        var problems = new List<string>(Scenario2CarryInFixture.Validate(_l0Definition));
        problems.AddRange(Scenario3EligibilityFixture.Validate(_l1Definition, Scenario3SpecValues.L1TripleCount));
        problems.AddRange(Scenario3EligibilityFixture.Validate(_l5Definition, Scenario3SpecValues.L5TripleCount));
        _fixtureProblems = problems;
        if (_fixtureProblems.Count > 0)
        {
            return;
        }

        var precheck = new List<string>();
        precheck.AddRange(Scenario3EligibilityFixture.CarryInStockConformityProblems(_l1Definition));
        precheck.AddRange(Scenario3EligibilityFixture.CarryInContinuationConformityProblems(_l1Definition));
        precheck.AddRange(Scenario3EligibilityFixture.CarryInStockConformityProblems(_l5Definition));
        precheck.AddRange(Scenario3EligibilityFixture.CarryInContinuationConformityProblems(_l5Definition));
        _precheckProblems = precheck;
        if (_precheckProblems.Count > 0)
        {
            return;
        }

        _l0Run = DeterministicRunner.Run(_l0Definition, Scenario3SpecValues.ScenarioName, Scenario3SpecValues.L0ArtifactName);
        _l1Run = DeterministicRunner.Run(_l1Definition, Scenario3SpecValues.ScenarioName, Scenario3SpecValues.L1ArtifactName);
        _l5Run = DeterministicRunner.Run(_l5Definition, Scenario3SpecValues.ScenarioName, Scenario3SpecValues.L5ArtifactName);

        _diffL0ToL1 = AutofillPlanAnalyzer.DiffAssignments(_l0Run.Plan, _l0Definition, _l1Run.Plan, _l1Definition);
        _diffL5ToL1 = AutofillPlanAnalyzer.DiffAssignments(_l5Run.Plan, _l5Definition, _l1Run.Plan, _l1Definition);

        WriteDiagnosis();
    }

    /// <summary>
    /// The precondition the specification section "Was die Fixture erzwingt" demands: the February
    /// carry-in and its expected continuations are keyword conform, verified BEFORE the autofill
    /// runs. When this is violated the engine runs are skipped entirely, because every downstream
    /// verdict would blame the algorithm for a fixture contradiction.
    /// </summary>
    [Test]
    public void CarryInPrecheck_FebruaryCarryInIsKeywordConform()
    {
        if (_fixtureProblems.Count > 0)
        {
            EnsureFixtureIsValid();
        }

        _precheckProblems.ShouldBeEmpty(
            "Precheck: the carry-in of the February fixture must be keyword conform — MA-1's night stock and "
            + "continuation, MA-2's late package, MA-3's early package, MA-4's rotated late package and MA-5's "
            + "rotated night package must all be open to their employees under the ban list. The autofill was NOT "
            + "run because the fixture itself contradicts the keyword table: "
            + string.Join(ProblemSeparator, _precheckProblems));
    }

    [Test]
    public void A16_NoBannedEmployeeHoldsANightShift()
    {
        var problems = new List<string>();
        var violations = Metrics.Keyword.Violations;
        if (violations.Count > 0)
        {
            problems.Add(
                $"{violations.Count.ToString(CultureInfo.InvariantCulture)} keyword violation(s): "
                + DescribeViolations(violations));
        }

        foreach (var employee in new[] { Scenario2SpecValues.Ma3, Scenario2SpecValues.Ma4 })
        {
            var counts = NightCountOf(employee);
            if (counts > 0)
            {
                problems.Add(
                    $"{employee} lacks {Scenario3SpecValues.NightKeyword} for the whole period and must hold zero "
                    + $"night shifts, but holds {counts.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        var ma2NightsAfterExpiry = Run.Plan.Tokens
            .Where(t => string.Equals(t.AgentId, Scenario2SpecValues.Ma2, StringComparison.Ordinal))
            .Where(t => t.ShiftRefId == AutofillShiftCatalog.NightShiftId)
            .Where(t => t.Date >= Scenario3SpecValues.Ma2NightBanFrom)
            .OrderBy(t => t.Date)
            .ToList();
        if (ma2NightsAfterExpiry.Count > 0)
        {
            problems.Add(
                $"{Scenario2SpecValues.Ma2}'s {Scenario3SpecValues.NightKeyword} is valid until "
                + $"{Scenario3SpecValues.Ma2NightValidUntil:yyyy-MM-dd}, so it must hold zero night shifts from "
                + $"{Scenario3SpecValues.Ma2NightBanFrom:yyyy-MM-dd} on, but holds "
                + $"{ma2NightsAfterExpiry.Count.ToString(CultureInfo.InvariantCulture)}: "
                + string.Join(", ", ma2NightsAfterExpiry.Select(t => $"{t.Date:yyyy-MM-dd}")));
        }

        problems.ShouldBeEmpty(
            "A16: keyword.violations must be empty — no employee may be planned onto a shift whose required "
            + "keyword it lacks, the carried-in February days included, because the scan deliberately covers the "
            + "locked stock the engine's own violation counter skips (finding K8). " + Describe(problems));
    }

    /// <summary>
    /// A17, reduced to two pinned measurements on 2026-08-12 (SPEC.md decision 11). The specification
    /// target of both parts is 0 and stays binding in tests/autofill/SPEC-SZENARIO3.md.
    /// <para>
    /// The two parts are NOT of the same nature. The first — every deviation carries a provable reason
    /// — cannot be reached at all as long as the engine records no rotation telemetry (finding K8b):
    /// the analyzer can only prove a reason where the fixture ban list closes the forward kind, so the
    /// remaining deviations stay unexplained no matter how well the engine rotates. It is pinned at
    /// <see cref="MaxUnexplainedRotationDeviations"/> as a count, and reaching 0 needs an engine change
    /// nobody has scheduled.
    /// </para>
    /// <para>
    /// The second is a REAL rotation defect and must be read as one: MA-3 and MA-4 can never work
    /// nights, so early to late to early is their only lawful rhythm and every one of their deviations
    /// should carry <see cref="RotationTransitionReason.KeywordIneligible"/>. The measurement of
    /// 2026-08-12 finds three Early to Early boundaries per employee — the engine simply repeats the
    /// early kind instead of rotating to late, which the ban list does not force and does not excuse.
    /// The pin <see cref="MaxUnprovenDeviationsPerNightBannedEmployee"/> stops that number from growing;
    /// it does not declare it acceptable.
    /// </para>
    /// </summary>
    [Test]
    public void A17_EveryRotationDeviationIsExplained()
    {
        var rotation = Metrics.Rotation;
        var problems = new List<string>();

        if (rotation.UnexplainedDeviations > MaxUnexplainedRotationDeviations)
        {
            var unexplained = rotation.Transitions
                .Where(t => string.Equals(t.Reason, RotationTransitionReason.Unexplained, StringComparison.Ordinal))
                .ToList();
            problems.Add(
                $"{rotation.UnexplainedDeviations.ToString(CultureInfo.InvariantCulture)} deviation(s) carry no "
                + $"provable reason, above the pinned "
                + $"{MaxUnexplainedRotationDeviations.ToString(CultureInfo.InvariantCulture)}: "
                + $"{Scenario2Diagnostics.DescribeTransitions(unexplained)}");
        }

        foreach (var employee in new[] { Scenario2SpecValues.Ma3, Scenario2SpecValues.Ma4 })
        {
            var wrongReason = rotation.Transitions
                .Where(t => string.Equals(t.Employee, employee, StringComparison.Ordinal))
                .Where(t => !t.Forward)
                .Where(t => !string.Equals(t.Reason, RotationTransitionReason.KeywordIneligible, StringComparison.Ordinal))
                .ToList();
            if (wrongReason.Count > MaxUnprovenDeviationsPerNightBannedEmployee)
            {
                problems.Add(
                    $"{employee} can never work nights, so each of its rotation deviations must carry the reason "
                    + $"{RotationTransitionReason.KeywordIneligible}, but "
                    + $"{wrongReason.Count.ToString(CultureInfo.InvariantCulture)} carry another, above the pinned "
                    + $"{MaxUnprovenDeviationsPerNightBannedEmployee.ToString(CultureInfo.InvariantCulture)}: "
                    + string.Join(", ", wrongReason.Select(t => $"{t.FromType} to {t.ToType} ({t.Reason})")));
            }
        }

        problems.ShouldBeEmpty(
            "A17: pinned measurement 2026-08-12 — rotation.unexplainedDeviations may reach "
            + $"{MaxUnexplainedRotationDeviations.ToString(CultureInfo.InvariantCulture)} and each of MA-3/MA-4 may "
            + $"carry {MaxUnprovenDeviationsPerNightBannedEmployee.ToString(CultureInfo.InvariantCulture)} deviation(s) "
            + "without the keyword reason. The specification target of both is 0 and stays binding: every departure "
            + "from early to late to night to early must be explained, and for MA-3/MA-4 the explanation is that the "
            + "ban list closes the night kind on every day of the period (early to late to early is their only "
            + "lawful rhythm). The first pin exists because the engine records no rotation reasons (finding K8b) and "
            + "no analyzer can prove one it never gets; the second pins a REAL rotation defect so it cannot grow. "
            + Describe(problems));
    }

    [Test]
    public void A18_SecondHalfNightsBelongToMa1AndMa5AndAreFullyStaffed()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            Scenario2SpecValues.Ma1,
            Scenario2SpecValues.Ma5,
        };
        var problems = new List<string>();
        var staffedDays = 0;

        for (var date = Scenario3SpecValues.Ma2NightBanFrom; date <= Definition.PeriodUntil; date = date.AddDays(1))
        {
            var day = date;
            var assignees = Run.Plan.Tokens
                .Where(t => t.ShiftRefId == AutofillShiftCatalog.NightShiftId && t.Date == day)
                .Select(t => t.AgentId)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();

            if (assignees.Count == 0)
            {
                problems.Add($"the night shift of {day:yyyy-MM-dd} is unstaffed");
                continue;
            }

            staffedDays++;
            var outsiders = assignees.Where(a => !allowed.Contains(a)).ToList();
            if (outsiders.Count > 0)
            {
                problems.Add(
                    $"the night shift of {day:yyyy-MM-dd} is held by {string.Join(", ", outsiders)}, who is not in "
                    + "the eligible pool");
            }
        }

        problems.ShouldBeEmpty(
            $"A18: from {Scenario3SpecValues.Ma2NightBanFrom:yyyy-MM-dd} on the night pool is exactly "
            + $"{{{Scenario2SpecValues.Ma1}, {Scenario2SpecValues.Ma5}}}, so all "
            + $"{Scenario3SpecValues.SecondHalfNightShifts.ToString(CultureInfo.InvariantCulture)} night shifts of "
            + "the second half must be staffed AND staffed by these two alone (staffed: "
            + $"{staffedDays.ToString(CultureInfo.InvariantCulture)}). " + Describe(problems));
    }

    /// <summary>
    /// A19, half of it reduced to a pinned measurement on 2026-08-12 (SPEC.md decision 11). The
    /// <c>forcedShortenings</c> bound stays SHARP at
    /// <see cref="Scenario3SpecValues.MaxForcedShortenings"/>: it follows from the pool arithmetic —
    /// 11 second-half nights on two employees fit into at most two full five-day packages — and is
    /// therefore provable, not aspirational. Only <c>unexplainedShortenings</c> is pinned, at
    /// <see cref="MaxUnexplainedShortenings"/>; its specification target of 0 stays binding and is
    /// coupled to the A5 short-package share and to the parked E4 recut.
    /// </summary>
    [Test]
    public void A19_PackageShorteningsStayWithinTheProvableBudget()
    {
        var packages = Metrics.Packages;
        var problems = new List<string>();

        if (packages.UnexplainedShortenings > MaxUnexplainedShortenings)
        {
            problems.Add(
                $"{packages.UnexplainedShortenings.ToString(CultureInfo.InvariantCulture)} shortened night "
                + "package(s) exceed what the pool arithmetic forces, above the pinned "
                + $"{MaxUnexplainedShortenings.ToString(CultureInfo.InvariantCulture)}");
        }

        if (packages.ForcedShortenings.Count > Scenario3SpecValues.MaxForcedShortenings)
        {
            problems.Add(
                $"{packages.ForcedShortenings.Count.ToString(CultureInfo.InvariantCulture)} forced shortening(s) "
                + $"were attributed, above the limit of "
                + $"{Scenario3SpecValues.MaxForcedShortenings.ToString(CultureInfo.InvariantCulture)}");
        }

        problems.ShouldBeEmpty(
            $"A19: {Scenario3SpecValues.SecondHalfNightShifts.ToString(CultureInfo.InvariantCulture)} second-half "
            + "nights on a pool of two employees fit into at most two full five-day packages (10 shifts), so "
            + "EXACTLY one shortened night package is unavoidable and any further shortening is the algorithm's "
            + "own doing. forcedShortenings stays asserted sharply at most "
            + $"{Scenario3SpecValues.MaxForcedShortenings.ToString(CultureInfo.InvariantCulture)} because the pool "
            + "arithmetic proves it; packages.unexplainedShortenings runs against the pinned measurement of "
            + $"2026-08-12, at most {MaxUnexplainedShortenings.ToString(CultureInfo.InvariantCulture)}, while its "
            + "specification target of 0 stays binding (SPEC.md). This is also the A5 adjustment of scenario 3 — "
            + "the inherited A5 was removed on 2026-08-12 and its measurements are pinned by the Baseline_ guards, "
            + "while the provable shortening is accounted here. Forced: "
            + string.Join(ProblemSeparator, packages.ForcedShortenings.Select(f =>
                $"{f.Employee} {f.StartDate:yyyy-MM-dd} ({f.LengthDays.ToString(CultureInfo.InvariantCulture)} d): "
                + f.ProvableCause))
            + " " + Describe(problems));
    }

    /// <summary>
    /// A24, pinned measurement 2026-08-12 evening (SPEC.md decision 13): changed assignments the night
    /// restriction cannot explain. The specification target is 0 and stays binding; the count — never
    /// the concrete slots, which are search-path noise — is pinned at 4, measured on the decision-12
    /// engine (down from 8 before the StartsOnDate fix).
    /// </summary>
    private const int MaxUnattributableChanges = 4;

    [Test]
    public void A24_EveryDifferenceToScenario2IsAttributableToTheNightRestriction()
    {
        var diff = DiffL0ToL1;
        var nightChanges = diff.ChangedAssignments
            .Where(c => c.ShiftType == AutofillShiftKind.Night)
            .ToList();
        var unattributable = diff.ChangedAssignments
            .Where(c => c.ShiftType != AutofillShiftKind.Night)
            .Where(c => !IsNightReshuffleFollowUp(c, nightChanges))
            .ToList();

        unattributable.Count.ShouldBeLessThanOrEqualTo(
            MaxUnattributableChanges,
            $"A24: the treatment differs from the control group in "
            + $"{diff.ChangedCount.ToString(CultureInfo.InvariantCulture)} slot(s); every changed assignment must "
            + "either be a night shift or the follow-up of a night reshuffle inside the same package — the same "
            + "employee, in a contiguous package whose span also contains a changed night. Changes without that "
            + "link mean the restriction rippled further than the causal chain allows (or the search simply walked "
            + "elsewhere, which the specification counts as unexplained). The specification target is 0; the count "
            + $"runs against the pinned measurement of 2026-08-12, at most "
            + $"{MaxUnattributableChanges.ToString(CultureInfo.InvariantCulture)}. Night changes: "
            + $"{nightChanges.Count.ToString(CultureInfo.InvariantCulture)}. Unattributable: "
            + DescribeChanges(unattributable));
    }

    /// <summary>
    /// A25, reduced to two pinned measurements on 2026-08-12 (SPEC.md decision 11). The specification
    /// target — no cohort member deviates more than
    /// <see cref="Scenario3SpecValues.CohortRelativeTolerance"/> relative to the cohort mean — stays
    /// binding in tests/autofill/SPEC-SZENARIO3.md and is quoted in the message. The MEASUREMENT is
    /// correct and was re-verified on 2026-08-12: finding K6 concerns the engine-internal
    /// normalisation only, while <c>EligibilityAnalyzer.BuildNightShares</c> respects the ban list, so
    /// MA-2's denominator of 20 eligible days is the right one. The deviation grew between stage 3 and
    /// today (14/6/11 nights to 17/3/11) as a side effect of E4d, E6 and the reach rework; the two
    /// pins below stop it from growing further without pretending the target is met.
    /// </summary>
    [Test]
    public void A25_NightSharesStayWithin20PercentOfTheCohortMean()
    {
        var shares = Metrics.Fairness.NightSharePerEligibleDay;
        var problems = new List<string>();

        var ma2 = shares.FirstOrDefault(
            s => string.Equals(s.Employee, Scenario2SpecValues.Ma2, StringComparison.Ordinal));
        if (ma2 is null || ma2.EligibleDays != Scenario3SpecValues.Ma2EligibleNightDays)
        {
            problems.Add(
                $"{Scenario2SpecValues.Ma2} must be normalised to "
                + $"{Scenario3SpecValues.Ma2EligibleNightDays.ToString(CultureInfo.InvariantCulture)} eligible "
                + $"night days, but the measurement reports "
                + $"{(ma2 is null ? "no entry" : ma2.EligibleDays.ToString(CultureInfo.InvariantCulture))}");
        }

        var cohort = shares.Where(s => s.EligibleDays > 0).ToList();
        var mean = cohort.Count == 0 ? 0 : cohort.Average(s => s.SharePerEligibleDay);
        foreach (var member in cohort)
        {
            var deviation = Math.Abs(member.SharePerEligibleDay - mean);
            if (deviation > MaxCohortDeviation + ShareEpsilon)
            {
                problems.Add(
                    $"{member.Employee} holds {member.NightShifts.ToString(CultureInfo.InvariantCulture)} nights on "
                    + $"{member.EligibleDays.ToString(CultureInfo.InvariantCulture)} eligible days = share "
                    + $"{member.SharePerEligibleDay.ToString("0.####", CultureInfo.InvariantCulture)}, deviating "
                    + $"{deviation.ToString("0.####", CultureInfo.InvariantCulture)} from the cohort mean "
                    + $"{mean.ToString("0.####", CultureInfo.InvariantCulture)}, above the pinned "
                    + $"{MaxCohortDeviation.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
        }

        if (Metrics.Fairness.CohortSpreadNight > MaxCohortSpreadNight + ShareEpsilon)
        {
            problems.Add(
                "cohortSpreadNight is "
                + $"{Metrics.Fairness.CohortSpreadNight.ToString("0.####", CultureInfo.InvariantCulture)}, above the "
                + $"pinned {MaxCohortSpreadNight.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        problems.ShouldBeEmpty(
            "A25: night fairness is measured inside the cohort of employees the ban list leaves any night day at "
            + "all — an equal-share comparison over all five is meaningless when two can never work nights. Pinned "
            + "measurement 2026-08-12: per eligible day no cohort member may deviate more than "
            + $"{MaxCohortDeviation.ToString("0.####", CultureInfo.InvariantCulture)} from the cohort mean and "
            + $"cohortSpreadNight may reach {MaxCohortSpreadNight.ToString("0.####", CultureInfo.InvariantCulture)}. "
            + "The specification target is unchanged and stricter (SPEC-SZENARIO3.md): no member deviates more than "
            + $"{(Scenario3SpecValues.CohortRelativeTolerance * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
            + $"relative to the mean. Measured cohortSpreadNight = "
            + $"{Metrics.Fairness.CohortSpreadNight.ToString("0.####", CultureInfo.InvariantCulture)}. "
            + Describe(problems));
    }

    /// <summary>
    /// The isolation property the L5 comparison can actually carry, reformulated on 2026-08-10. Before
    /// MA-2's expiry the treatment L1 and the diagnostic run L5 must agree on three hard properties:
    /// the same slots are covered by the same number of employees, neither run holds a legality
    /// violation the other does not hold, and the frozen carry-in prefix is untouched by both and
    /// identical between them. WHO holds an already covered slot is explicitly the optimiser's freedom
    /// and is NOT asserted — which is a change of the assertion, not a weakening of it: cell identity
    /// and A25 cannot both hold. The only period-wide term of the whole fitness,
    /// <c>ComputeShiftKindFairnessScore</c>, normalises every shift kind by the eligible days
    /// <c>GetKindEligibility</c> counts over the ENTIRE period, so MA-2's night denominator is 20 under
    /// L1 against 31 under L5; the same pre-cut night is worth a different share in the two runs, and
    /// because stage 4 decides every tie of stages 0 to 3, the restriction re-ranks hour-neutral
    /// redistributions before its first day of validity. Measured under E4d it did so while preventing
    /// no assignment at all. That channel is measured in
    /// <see cref="Scenario3IsolationChannelDiagnosticsTests"/>; A25 asserts the very same normalisation.
    /// </summary>
    [Test]
    public void L5Isolation_BeforeMarch21OnlyCoverageLegalityAndTheFrozenPrefixMustMatch()
    {
        var preCutChanges = DiffL5ToL1.ChangedAssignments
            .Where(c => c.Date < Scenario3SpecValues.Ma2NightBanFrom)
            .ToList();
        WriteReassignmentDiagnosis(preCutChanges);

        var problems = new List<string>();
        AddPreCutCoverageProblems(problems, preCutChanges);
        AddPreCutLegalityProblems(problems);
        AddFrozenPrefixProblems(problems);

        problems.ShouldBeEmpty(
            $"L5 isolation: before {Scenario3SpecValues.Ma2NightBanFrom:yyyy-MM-dd} the treatment L1 and the "
            + "diagnostic run L5 must agree on the three properties a restriction that only starts later cannot "
            + "legitimately move — coverage (the same slots staffed by the same number of employees; over the whole "
            + $"period L1 staffs {Metrics.Coverage.FilledShifts.ToString(CultureInfo.InvariantCulture)} of "
            + $"{Metrics.Coverage.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)}, L5 staffs "
            + $"{DiagnosticRun.Metrics.Coverage.FilledShifts.ToString(CultureInfo.InvariantCulture)} of "
            + $"{DiagnosticRun.Metrics.Coverage.TotalRequiredShifts.ToString(CultureInfo.InvariantCulture)}), "
            + "legality (no rest-time, night-to-early, double-booking or keyword violation exists in one run and "
            + "not in the other) and the frozen prefix (both runs leave the February carry-in byte-identical, start "
            + "from the same carry-in and place no token outside the period). WHO holds an already covered slot is "
            + "NOT asserted and is not a finding: the only period-wide term of the fitness, "
            + "ComputeShiftKindFairnessScore, normalises by the eligible days GetKindEligibility counts over the "
            + "whole period (MA-2's night denominator 20 under L1 against 31 under L5), so the restriction re-ranks "
            + "hour-neutral redistributions before its own first day of validity — the channel measured in "
            + nameof(Scenario3IsolationChannelDiagnosticsTests) + " and the same normalisation A25 demands. "
            + $"Assignment differences before the cut: "
            + $"{preCutChanges.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{DiffL5ToL1.ChangedCount.ToString(CultureInfo.InvariantCulture)} over the whole period, reported to "
            + "the test output as information. " + Describe(problems));
    }

    private void AddPreCutCoverageProblems(List<string> problems, IReadOnlyList<ChangedAssignment> preCutChanges)
    {
        var coverageChanges = preCutChanges
            .Where(c => AssigneeCountOf(c.EmployeeBaseline) != AssigneeCountOf(c.EmployeeTreatment))
            .ToList();
        if (coverageChanges.Count > 0)
        {
            problems.Add(
                $"{coverageChanges.Count.ToString(CultureInfo.InvariantCulture)} slot(s) before the cut carry a "
                + "different number of employees in the two runs: " + DescribeChanges(coverageChanges));
        }

        AddSetDifference(
            problems,
            "unstaffed slot",
            UnfilledKeysBeforeCut(Metrics),
            UnfilledKeysBeforeCut(DiagnosticRun.Metrics));
    }

    private void AddPreCutLegalityProblems(List<string> problems)
    {
        AddSetDifference(problems, "rest-time violation", RestKeysBeforeCut(Metrics), RestKeysBeforeCut(DiagnosticRun.Metrics));
        AddSetDifference(
            problems,
            "night-to-early violation",
            NightToEarlyKeysBeforeCut(Metrics),
            NightToEarlyKeysBeforeCut(DiagnosticRun.Metrics));
        AddSetDifference(
            problems,
            "double booking",
            DoubleBookingKeysBeforeCut(Metrics),
            DoubleBookingKeysBeforeCut(DiagnosticRun.Metrics));
        AddSetDifference(
            problems,
            "keyword violation",
            KeywordKeysBeforeCut(Metrics),
            KeywordKeysBeforeCut(DiagnosticRun.Metrics));
    }

    private void AddFrozenPrefixProblems(List<string> problems)
    {
        AddCarryInProblem(problems, L1Label, Run);
        AddCarryInProblem(problems, L5Label, DiagnosticRun);

        var boundaryDifference = PlanFingerprint.FirstDifference(
            PlanFingerprint.ForBoundaryWorks(Definition.Context.BoundaryLockedWorks),
            PlanFingerprint.ForBoundaryWorks(DiagnosticDefinition.Context.BoundaryLockedWorks));
        if (boundaryDifference is not null)
        {
            problems.Add($"the two runs do not even start from the same carry-in: {boundaryDifference}");
        }

        AddOutsidePeriodProblem(problems, L1Label, Run, Definition);
        AddOutsidePeriodProblem(problems, L5Label, DiagnosticRun, DiagnosticDefinition);
    }

    private static void AddCarryInProblem(List<string> problems, string label, DeterministicRunResult run)
    {
        if (!run.CarryInUnchanged)
        {
            problems.Add($"{label} changed the fixed previous-month works: {run.CarryInDifference}");
        }
    }

    private static void AddOutsidePeriodProblem(
        List<string> problems, string label, DeterministicRunResult run, AutofillScenarioDefinition definition)
    {
        var outside = AutofillPlanAnalyzer.TokensOutsidePeriod(run.Plan, definition);
        if (outside.Count > 0)
        {
            problems.Add(
                $"{label} placed {outside.Count.ToString(CultureInfo.InvariantCulture)} token(s) outside the "
                + "period: " + string.Join(ProblemSeparator, outside.Select(t => $"{t.AgentId} {t.Date:yyyy-MM-dd}")));
        }
    }

    private static void AddSetDifference(
        List<string> problems, string label, IReadOnlyList<string> treatment, IReadOnlyList<string> diagnostic)
    {
        var onlyInTreatment = treatment.Except(diagnostic, StringComparer.Ordinal).ToList();
        if (onlyInTreatment.Count > 0)
        {
            problems.Add($"{label}(s) only in {L1Label}: {string.Join(", ", onlyInTreatment)}");
        }

        var onlyInDiagnostic = diagnostic.Except(treatment, StringComparer.Ordinal).ToList();
        if (onlyInDiagnostic.Count > 0)
        {
            problems.Add($"{label}(s) only in {L5Label}: {string.Join(", ", onlyInDiagnostic)}");
        }
    }

    private static IReadOnlyList<string> UnfilledKeysBeforeCut(AutofillMetrics metrics)
        => metrics.Coverage.UnfilledShifts
            .Where(u => u.Date < Scenario3SpecValues.Ma2NightBanFrom)
            .Select(u => $"{u.Date:yyyy-MM-dd} {u.ShiftType} order {u.Order.ToString(CultureInfo.InvariantCulture)}")
            .ToList();

    private static IReadOnlyList<string> RestKeysBeforeCut(AutofillMetrics metrics)
        => metrics.Legality.RestViolations
            .Where(v => v.DateFrom < Scenario3SpecValues.Ma2NightBanFrom)
            .Select(v => $"{v.Employee} {v.DateFrom:yyyy-MM-dd} {v.FromShift} to {v.DateTo:yyyy-MM-dd} {v.ToShift}")
            .ToList();

    private static IReadOnlyList<string> NightToEarlyKeysBeforeCut(AutofillMetrics metrics)
        => metrics.Legality.NightToEarlyViolations
            .Where(v => v.Date < Scenario3SpecValues.Ma2NightBanFrom)
            .Select(v => $"{v.Employee} {v.Date:yyyy-MM-dd}")
            .ToList();

    private static IReadOnlyList<string> DoubleBookingKeysBeforeCut(AutofillMetrics metrics)
        => metrics.Coverage.DoubleBookings
            .Where(b => b.Date < Scenario3SpecValues.Ma2NightBanFrom)
            .Select(b => $"{b.Employee} {b.Date:yyyy-MM-dd} {b.Reason}")
            .ToList();

    private static IReadOnlyList<string> KeywordKeysBeforeCut(AutofillMetrics metrics)
        => metrics.Keyword.Violations
            .Where(v => v.Date < Scenario3SpecValues.Ma2NightBanFrom)
            .Select(v => $"{v.Employee} {v.Date:yyyy-MM-dd} {v.ShiftType} missing {v.MissingKeyword}")
            .ToList();

    private static int AssigneeCountOf(string? assignees) => EmployeesOf(assignees).Count();

    private void WriteReassignmentDiagnosis(IReadOnlyList<ChangedAssignment> preCutChanges)
        => TestContext.Out.WriteLine(
            "L5 isolation, information only — who holds a covered slot is optimiser freedom and is not asserted: "
            + $"{preCutChanges.Count.ToString(CultureInfo.InvariantCulture)} assignment difference(s) before "
            + $"{Scenario3SpecValues.Ma2NightBanFrom:yyyy-MM-dd}: " + DescribeChanges(preCutChanges));

    [Test]
    public void A26_AllRunsOfTheFamilyAreDeterministic()
    {
        var problems = new List<string>();
        AddDeterminismProblem(problems, L0Label, ControlRun);
        AddDeterminismProblem(problems, L1Label, Run);
        AddDeterminismProblem(problems, L5Label, DiagnosticRun);

        problems.ShouldBeEmpty(
            "A26: every run of the family is executed twice with the same seed, sequential evaluation and no "
            + "wall-clock budget and must reproduce itself byte-identically; L4 is judged in its own fixture. "
            + Describe(problems));
    }

    private static void AddDeterminismProblem(List<string> problems, string label, DeterministicRunResult run)
    {
        if (!run.RunsIdentical)
        {
            problems.Add($"{label} differs from its own repetition at {run.FirstDifference}");
        }
    }

    private static string DescribeViolations(IEnumerable<KeywordViolation> violations)
        => string.Join(ProblemSeparator, violations.Select(v =>
            $"{v.Employee} {v.Date:yyyy-MM-dd} {v.ShiftType} missing {v.MissingKeyword}"
            + (v.ValidUntil is null ? string.Empty : $" (valid until {v.ValidUntil:yyyy-MM-dd})")));

    private static string DescribeChanges(IReadOnlyList<ChangedAssignment> changes)
        => changes.Count == 0
            ? "none"
            : string.Join(ProblemSeparator, changes.Select(c =>
                $"{c.Date:yyyy-MM-dd} {c.ShiftType}: {c.EmployeeBaseline ?? "unstaffed"} to "
                + $"{c.EmployeeTreatment ?? "unstaffed"}"));

    private static IEnumerable<string> EmployeesOf(string? assignees)
        => assignees?.Split(AssigneeJoinSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];

    private int NightCountOf(string employee)
        => Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => string.Equals(c.Employee, employee, StringComparison.Ordinal))
            .Sum(c => c.Night);

    private bool IsNightReshuffleFollowUp(ChangedAssignment change, IReadOnlyList<ChangedAssignment> nightChanges)
    {
        foreach (var employee in EmployeesOf(change.EmployeeBaseline))
        {
            if (PackageLinksToNightChange(ControlRun.Metrics, employee, change.Date, nightChanges))
            {
                return true;
            }
        }

        foreach (var employee in EmployeesOf(change.EmployeeTreatment))
        {
            if (PackageLinksToNightChange(Run.Metrics, employee, change.Date, nightChanges))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PackageLinksToNightChange(
        AutofillMetrics metrics,
        string employee,
        DateOnly date,
        IReadOnlyList<ChangedAssignment> nightChanges)
    {
        var containing = Scenario2Diagnostics.PackagesOf(metrics, employee)
            .Where(p => p.StartDate <= date && p.EndDate >= date);

        return containing.Any(p => nightChanges.Any(n =>
            n.Date >= p.StartDate
            && n.Date <= p.EndDate
            && (EmployeesOf(n.EmployeeBaseline).Contains(employee, StringComparer.Ordinal)
                || EmployeesOf(n.EmployeeTreatment).Contains(employee, StringComparer.Ordinal))));
    }

    private void WriteDiagnosis()
    {
        TestContext.Out.WriteLine(
            $"scenario 3, seed {_l1Definition!.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + $"ban triples L1={_l1Definition.Eligibility!.IneligibleAssignments.Count.ToString(CultureInfo.InvariantCulture)} "
            + $"L5={_l5Definition!.Eligibility!.IneligibleAssignments.Count.ToString(CultureInfo.InvariantCulture)}");
        TestContext.Out.WriteLine(
            $"diff L0-L1: {_diffL0ToL1!.ChangedCount.ToString(CultureInfo.InvariantCulture)} slot(s); "
            + $"diff L5-L1: {_diffL5ToL1!.ChangedCount.ToString(CultureInfo.InvariantCulture)} slot(s)");
        TestContext.Out.WriteLine("L1 carry-in: " + Scenario2Diagnostics.DescribeCarryIn(_l1Run!.Metrics.CarryIn));

        foreach (var path in _l0Run!.ArtifactPaths.Concat(_l1Run.ArtifactPaths).Concat(_l5Run!.ArtifactPaths))
        {
            TestContext.Out.WriteLine("artifact: " + path);
        }
    }

    /// <summary>
    /// Scenario-3 declaration of assertion A12, asserting the specification value
    /// <see cref="AutofillSpecConstants.MinRestHoursAfterCarryIn"/> — since the owner ruling of
    /// 2026-08-12 (SPEC.md decision 12d) the rest is measured in hours from shift end to shift start.
    /// The core lives in <see cref="Scenario2AssertionsBase"/>.
    /// </summary>
    [Test]
    public void A12_RestHoursFollowTheCompletedCarryInPackage()
        => AssertA12RestHoursFollowTheCompletedCarryInPackage(AutofillSpecConstants.MinRestHoursAfterCarryIn);

    private void EnsureFixtureIsValid()
    {
        if (_fixtureProblems.Count > 0)
        {
            Assert.Inconclusive(
                "fixture invalid — scenario 3 was not run, so no rule assertion can be judged: "
                + string.Join(ProblemSeparator, _fixtureProblems));
        }

        if (_precheckProblems.Count > 0)
        {
            Assert.Inconclusive(
                "carry-in precheck failed — the autofill was not run; see "
                + nameof(CarryInPrecheck_FebruaryCarryInIsKeywordConform) + " for the verdict: "
                + string.Join(ProblemSeparator, _precheckProblems));
        }
    }
}
