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
/// executed twice by the deterministic runner, which is what A26 reads. A1 to A14 are inherited from
/// <see cref="Scenario2AssertionsBase"/> and judge L1, exactly as the specification inherits them
/// onto the main run.
/// <para>
/// What these runs can and cannot prove: the engine knows only the bare ban set, so L1 exercises the
/// POOL behaviour under the restriction and presupposes the expiry semantics of MA-2's keyword
/// (findings K3/K4). A20/A21/A23 and the expiry proof itself run on API level against
/// EligibilityMatrixBuilder and are deliberately absent here; A22 runs in
/// <see cref="Scenario3UnstaffableNightTests"/> on the L4 fixture. Expectations come from the
/// specification, never from observed behaviour — A17 is expected red in the same way A7 is, and
/// stays asserted at the specification value as finding documentation.
/// </para>
/// <para>
/// The inherited <c>Baseline_</c> guards read the inert placeholder pins of
/// <see cref="Scenario3BaselineValues"/> until a first band measurement exists; green there means
/// "not pinned yet". No seed band is measured for this family to keep the run count bounded.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
public class Scenario3MainRunTests : Scenario2AssertionsBase
{
    /// <summary>Separator the analyzer uses when one slot holds several assignees.</summary>
    private const char AssigneeJoinSeparator = '+';

    private const double ShareEpsilon = 1e-9;

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

    [Test]
    public void A17_EveryRotationDeviationIsExplained()
    {
        var rotation = Metrics.Rotation;
        var problems = new List<string>();

        if (rotation.UnexplainedDeviations > 0)
        {
            var unexplained = rotation.Transitions
                .Where(t => string.Equals(t.Reason, RotationTransitionReason.Unexplained, StringComparison.Ordinal))
                .ToList();
            problems.Add(
                $"{rotation.UnexplainedDeviations.ToString(CultureInfo.InvariantCulture)} deviation(s) carry no "
                + $"provable reason: {Scenario2Diagnostics.DescribeTransitions(unexplained)}");
        }

        foreach (var employee in new[] { Scenario2SpecValues.Ma3, Scenario2SpecValues.Ma4 })
        {
            var wrongReason = rotation.Transitions
                .Where(t => string.Equals(t.Employee, employee, StringComparison.Ordinal))
                .Where(t => !t.Forward)
                .Where(t => !string.Equals(t.Reason, RotationTransitionReason.KeywordIneligible, StringComparison.Ordinal))
                .ToList();
            if (wrongReason.Count > 0)
            {
                problems.Add(
                    $"{employee} can never work nights, so each of its rotation deviations must carry the reason "
                    + $"{RotationTransitionReason.KeywordIneligible}, but "
                    + $"{wrongReason.Count.ToString(CultureInfo.InvariantCulture)} carry another: "
                    + string.Join(", ", wrongReason.Select(t => $"{t.FromType} to {t.ToType} ({t.Reason})")));
            }
        }

        problems.ShouldBeEmpty(
            "A17: rotation.unexplainedDeviations must be 0 — every departure from early to late to night to early "
            + "must be explained, and for MA-3/MA-4 the explanation is that the ban list closes the night kind on "
            + "every day of the period (early to late to early is their only lawful rhythm). Expected red in the "
            + "same way A7 is: the engine records no rotation reasons (finding K8b), so any deviation the ban list "
            + "does not prove stays unexplained. " + Describe(problems));
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

    [Test]
    public void A19_PackageShorteningsStayWithinTheProvableBudget()
    {
        var packages = Metrics.Packages;
        var problems = new List<string>();

        if (packages.UnexplainedShortenings > 0)
        {
            problems.Add(
                $"{packages.UnexplainedShortenings.ToString(CultureInfo.InvariantCulture)} shortened night "
                + "package(s) exceed what the pool arithmetic forces");
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
            + "own doing: packages.unexplainedShortenings must be 0 and forcedShortenings at most "
            + $"{Scenario3SpecValues.MaxForcedShortenings.ToString(CultureInfo.InvariantCulture)}. This is also the "
            + "A5 adjustment of scenario 3 — A5 itself stays inherited unchanged and the provable shortening is "
            + "accounted here. Forced: "
            + string.Join(ProblemSeparator, packages.ForcedShortenings.Select(f =>
                $"{f.Employee} {f.StartDate:yyyy-MM-dd} ({f.LengthDays.ToString(CultureInfo.InvariantCulture)} d): "
                + f.ProvableCause))
            + " " + Describe(problems));
    }

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

        unattributable.ShouldBeEmpty(
            $"A24: the treatment differs from the control group in "
            + $"{diff.ChangedCount.ToString(CultureInfo.InvariantCulture)} slot(s); every changed assignment must "
            + "either be a night shift or the follow-up of a night reshuffle inside the same package — the same "
            + "employee, in a contiguous package whose span also contains a changed night. Changes without that "
            + "link mean the restriction rippled further than the causal chain allows (or the search simply walked "
            + $"elsewhere, which the specification counts as unexplained). Night changes: "
            + $"{nightChanges.Count.ToString(CultureInfo.InvariantCulture)}. Unattributable: "
            + DescribeChanges(unattributable));
    }

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
            if (deviation > (Scenario3SpecValues.CohortRelativeTolerance * mean) + ShareEpsilon)
            {
                problems.Add(
                    $"{member.Employee} holds {member.NightShifts.ToString(CultureInfo.InvariantCulture)} nights on "
                    + $"{member.EligibleDays.ToString(CultureInfo.InvariantCulture)} eligible days = share "
                    + $"{member.SharePerEligibleDay.ToString("0.####", CultureInfo.InvariantCulture)}, deviating "
                    + $"{deviation.ToString("0.####", CultureInfo.InvariantCulture)} from the cohort mean "
                    + $"{mean.ToString("0.####", CultureInfo.InvariantCulture)}");
            }
        }

        problems.ShouldBeEmpty(
            "A25: night fairness is measured inside the cohort of employees the ban list leaves any night day at "
            + $"all — an equal-share comparison over all five is meaningless when two can never work nights. Per "
            + "eligible day, no cohort member may deviate more than "
            + $"{(Scenario3SpecValues.CohortRelativeTolerance * 100).ToString("0.#", CultureInfo.InvariantCulture)} % "
            + $"from the cohort mean; cohortSpreadNight = "
            + $"{Metrics.Fairness.CohortSpreadNight.ToString("0.####", CultureInfo.InvariantCulture)}. "
            + Describe(problems));
    }

    [Test]
    public void L5Isolation_DifferencesToL1StartOnlyAtMarch21()
    {
        var early = DiffL5ToL1.ChangedAssignments
            .Where(c => c.Date < Scenario3SpecValues.Ma2NightBanFrom)
            .ToList();

        early.ShouldBeEmpty(
            "L5 isolation: L5 differs from L1 only in MA-2's expiry — its 11 extra ban triples all lie on "
            + $"{Scenario3SpecValues.Ma2NightBanFrom:yyyy-MM-dd}..{Definition.PeriodUntil:yyyy-MM-dd} — so every "
            + "assignment difference before that date means the validity evaluation reaches back in time or the "
            + "algorithm replans ahead of the restriction without declaring it; both are findings per the "
            + $"specification. Total differences: "
            + $"{DiffL5ToL1.ChangedCount.ToString(CultureInfo.InvariantCulture)}. Differences before the cut: "
            + DescribeChanges(early));
    }

    [Test]
    public void A26_AllRunsOfTheFamilyAreDeterministic()
    {
        var problems = new List<string>();
        AddDeterminismProblem(problems, "L0", ControlRun);
        AddDeterminismProblem(problems, "L1", Run);
        AddDeterminismProblem(problems, "L5", DiagnosticRun);

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
