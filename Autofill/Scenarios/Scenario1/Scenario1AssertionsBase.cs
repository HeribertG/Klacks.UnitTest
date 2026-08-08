// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Analysis;
using Klacks.UnitTest.Autofill.Analysis.Model;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// Assertions A1 to A9 of the clean-start scenario, shared by the specification run (180 guaranteed
/// hours) and the calibration variant 1b (150 guaranteed hours). The scenario is built and run
/// exactly once per fixture in <see cref="BuildAndRunScenarioOnce"/>; every assertion then reads the
/// cached measurement, so one test method per assertion id yields a complete pass/fail table instead
/// of stopping at the first failure.
/// <para>
/// Arithmetic framing (the full text lives on <see cref="AutofillSpecConstants"/>): 31 days x 3
/// shifts = 93 shifts = 744 h of demand. With 180 guaranteed hours the five employees want 112.5
/// shifts, so 19.5 shifts (156 h) of demand are missing and a clean 5-work/2-free rhythm for all five
/// at once is arithmetically impossible; rule 6 therefore predicts ranks 1-4 near 176 h and rank 5
/// near 40 h. Variant 1b lowers the target to 150 h (750 h wanted against 744 h available), which
/// makes all goals satisfiable at the same time and separates real algorithm defects from unavoidable
/// goal conflicts.
/// </para>
/// <para>
/// Two deliberate readings of the specification. A5 measures "no package longer than 5" against the
/// specification value 5 (<see cref="AutofillSpecConstants.MaxAllowedPackageLength"/>), not against
/// the contractual hard cap of 6 consecutive days the engine enforces. A7 asserts the forward
/// rotation rate only; whether a backward or skipping transition was forced cannot be derived from a
/// finished plan, so every such transition is listed in the failure message and judged by hand.
/// </para>
/// </summary>
public abstract class Scenario1AssertionsBase : AutofillBaselineTestBase
{
    private const double HoursComparisonEpsilon = 1e-9;

    private const double ShareComparisonEpsilon = 1e-9;

    private const string PercentFormat = "P1";

    private const string HoursFormat = "0.##";

    private const string ShareFormat = "0.###";

    private const string DateFormat = "yyyy-MM-dd";

    private const string ShortDateFormat = "MM-dd";

    private AutofillScenarioDefinition _definition = null!;

    private DeterministicRunResult _run = null!;

    /// <summary>Artifact folder of this fixture.</summary>
    protected abstract string ScenarioName { get; }

    /// <summary>Artifact file name of this fixture; a literal, because it becomes a file name.</summary>
    protected abstract string ArtifactTestName { get; }

    /// <summary>Guaranteed hours every employee of this fixture carries.</summary>
    protected abstract double GuaranteedHours { get; }

    /// <summary>Hours A6 measures the 95 % threshold against.</summary>
    protected abstract double ReferenceHours { get; }

    /// <summary>Highest list rank the hour and fairness expectations of A6 and A8 apply to.</summary>
    protected abstract int HighestAssertedRank { get; }

    protected AutofillScenarioDefinition Definition => _definition;

    protected DeterministicRunResult Run => _run;

    protected AutofillMetrics Metrics => _run.Metrics;

    /// <summary>The no-regression floor reads the same cached measurement as the rule assertions.</summary>
    protected override AutofillMetrics BaselineMetrics => Metrics;

    [OneTimeSetUp]
    public void BuildAndRunScenarioOnce()
    {
        _definition = new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, GuaranteedHours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)
            .Build();

        var fixtureProblems = ValidateFixture(_definition, GuaranteedHours);
        if (fixtureProblems.Count > 0)
        {
            throw new InvalidOperationException(
                "The clean-start fixture does not match the specification, so no assertion below would mean "
                + "anything:" + Environment.NewLine + string.Join(Environment.NewLine, fixtureProblems));
        }

        _run = DeterministicRunner.Run(_definition, ScenarioName, ArtifactTestName);
        WriteDiagnosis();
        MeasureAndReportSeedBand(_definition, _run.Metrics, ScenarioName, ArtifactTestName);
    }

    [Test]
    public void A1_EveryDemandedShiftIsStaffed()
    {
        var coverage = Metrics.Coverage;
        var unfilled = coverage.UnfilledShifts
            .Select(u => $"{u.Date.ToString(DateFormat, CultureInfo.InvariantCulture)} {u.ShiftType}")
            .ToList();

        unfilled.ShouldBeEmpty(
            $"A1: all {coverage.TotalRequiredShifts} demanded shifts must be staffed, but only "
            + $"{coverage.FilledShifts} are. Unstaffed slots (date, kind): {Join(unfilled)}. "
            + $"Diagnosis only, not part of A1: {coverage.OversuppliedSlots} slot(s) received more employees "
            + "than they demand.");
    }

    [Test]
    public void A2_NoEmployeeHoldsTwoShiftsOnTheSameDay()
    {
        var doubleBookings = Metrics.Coverage.DoubleBookings
            .Select(d =>
                $"{d.Employee} (rank {Definition.ListRankOf(d.Employee)}) on "
                + $"{d.Date.ToString(DateFormat, CultureInfo.InvariantCulture)}: {string.Join(" + ", d.Shifts)}")
            .ToList();

        doubleBookings.ShouldBeEmpty(
            $"A2: no employee may hold more than one shift on one calendar day, but {doubleBookings.Count} "
            + $"day(s) are double booked: {Join(doubleBookings)}");
    }

    [Test]
    public void A3_RestTimeAndNightToEarlyAreRespected()
    {
        var legality = Metrics.Legality;
        var problems = new List<string>();

        foreach (var violation in legality.RestViolations)
        {
            problems.Add(
                $"rest time: {violation.Employee} (rank {Definition.ListRankOf(violation.Employee)}) "
                + $"{violation.FromShift} on {violation.DateFrom.ToString(DateFormat, CultureInfo.InvariantCulture)} "
                + $"to {violation.ToShift} on {violation.DateTo.ToString(DateFormat, CultureInfo.InvariantCulture)}: "
                + $"gap {violation.GapHours.ToString(HoursFormat, CultureInfo.InvariantCulture)} h, required at least "
                + $"{AutofillSpecConstants.MinRestHours.ToString(HoursFormat, CultureInfo.InvariantCulture)} h");
        }

        foreach (var violation in legality.NightToEarlyViolations)
        {
            problems.Add(
                $"night to early: {violation.Employee} (rank {Definition.ListRankOf(violation.Employee)}) works the "
                + $"night of {violation.Date.ToString(DateFormat, CultureInfo.InvariantCulture)} and the early shift "
                + $"of {violation.Date.AddDays(1).ToString(DateFormat, CultureInfo.InvariantCulture)}");
        }

        problems.ShouldBeEmpty(
            $"A3: the plan must contain no rest-time violation and no night-to-early sequence, but "
            + $"{legality.RestViolations.Count} rest-time and {legality.NightToEarlyViolations.Count} "
            + $"night-to-early violation(s) were found: {Join(problems)}");
    }

    [Test]
    public void A4_ShiftKindStaysConstantInsideAPackage()
    {
        var mixed = DescribeMixedPackages();

        Metrics.Packages.MixedTypeCount.ShouldBe(
            0,
            $"A4: the shift kind must stay the same inside a package, but {Metrics.Packages.MixedTypeCount} of "
            + $"{Metrics.Packages.Items.Count} package(s) change kind: {Join(mixed)}");
    }

    [Test]
    public void A5_PackageLengthsFollowTheFiveTwoIdeal()
    {
        var packages = Metrics.Packages;
        var histogram = FormatHistogram(packages.LengthHistogram);
        var problems = new List<string>();

        problems.AddRange(DescribeLengthModeProblem());
        problems.AddRange(DescribeShortPackageProblem());
        problems.AddRange(DescribeOverlongPackages());

        problems.ShouldBeEmpty(
            $"A5: the package lengths must peak at {AutofillSpecConstants.ExpectedPackageLengthMode} days, keep the "
            + $"share of packages of at most {AutofillSpecConstants.ShortPackageMaxLength} days below "
            + $"{FormatShare(AutofillSpecConstants.ShortPackageShareLimit)} (tolerance "
            + $"{FormatShare(AutofillSpecConstants.ShortPackageShareTolerance)}) and contain no package longer than "
            + $"{AutofillSpecConstants.MaxAllowedPackageLength} days. Failures: {Join(problems)}. "
            + $"lengthHistogram={histogram}, packages={packages.Items.Count}, "
            + $"idealShare={FormatShare(packages.IdealShare)}, freeBlockHistogram={FormatFreeBlocks()}");
    }

    [Test]
    public void A6_GuaranteedHoursAreServedTopDown()
    {
        var hours = Metrics.Hours;
        var required = ReferenceHours * AutofillSpecConstants.FulfilmentThreshold;
        var problems = new List<string>();

        foreach (var violation in hours.MonotonicityViolations)
        {
            problems.Add(
                $"monotonicity: rank {violation.Rank} reaches {FormatPercent(violation.ThisPct)} while rank "
                + $"{violation.Rank - 1} above it only reaches {FormatPercent(violation.PrevPct)}");
        }

        foreach (var employee in hours.PerEmployee.Where(e => e.ListRank <= HighestAssertedRank))
        {
            if (employee.PlannedHours + HoursComparisonEpsilon < required)
            {
                problems.Add(
                    $"{employee.Employee} (rank {employee.ListRank}): planned "
                    + $"{FormatHours(employee.PlannedHours)} h, required at least {FormatHours(required)} h "
                    + $"({FormatPercent(AutofillSpecConstants.FulfilmentThreshold)} of the "
                    + $"{FormatHours(ReferenceHours)} h reference), fulfilment of the "
                    + $"{FormatHours(employee.GuaranteedHours)} h guaranteed = "
                    + $"{FormatPercent(employee.FulfillmentPct)}");
            }
        }

        problems.ShouldBeEmpty(
            $"A6: the fulfilment must never rise down the list and ranks 1 to {HighestAssertedRank} must reach at "
            + $"least {FormatHours(required)} h. Failures: {Join(problems)}. Measured: {FormatHoursPerEmployee()}");
    }

    [Test]
    public void A7_RotationFollowsEarlyLateNight()
    {
        var rotation = Metrics.Rotation;
        var nonForward = DescribeNonForwardTransitions();

        rotation.ForwardRate.ShouldBeGreaterThanOrEqualTo(
            AutofillSpecConstants.MinForwardRotationRate,
            $"A7: at least {FormatPercent(AutofillSpecConstants.MinForwardRotationRate)} of the package transitions "
            + $"must follow early to late to night to early, but only {FormatPercent(rotation.ForwardRate)} do "
            + $"({rotation.Transitions.Count - rotation.BackwardOrSkipCount} of {rotation.Transitions.Count}). "
            + $"Backward, skipping or repeating transitions: {Join(nonForward)}. The forced flag is always false "
            + "because a finished plan does not record whether the forward successor was still available; each of "
            + "these transitions has to be judged by hand.");
    }

    [Test]
    public void A8_ShiftKindsAreSpreadEvenlyOverTheEmployees()
    {
        var counts = Metrics.Fairness.ShiftTypeCountPerEmployee
            .Where(c => c.ListRank <= HighestAssertedRank)
            .ToList();
        var spread = AutofillPlanAnalyzer.SpreadOf(counts);
        var problems = new List<string>();

        AddSpreadProblem(problems, AutofillShiftKind.Early, spread.Early);
        AddSpreadProblem(problems, AutofillShiftKind.Late, spread.Late);
        AddSpreadProblem(problems, AutofillShiftKind.Night, spread.Night);

        problems.ShouldBeEmpty(
            $"A8: over ranks 1 to {HighestAssertedRank} no shift kind may differ by more than "
            + $"{AutofillSpecConstants.MaxShiftKindSpread} shift(s) between the employee holding the most and the "
            + $"one holding the fewest. Failures: {Join(problems)}. Counts: {FormatShiftKindCounts(counts)}. "
            + $"Spread over the asserted ranks: early={spread.Early}, late={spread.Late}, night={spread.Night}; "
            + $"over all employees: early={Metrics.Fairness.SpreadPerType.Early}, "
            + $"late={Metrics.Fairness.SpreadPerType.Late}, night={Metrics.Fairness.SpreadPerType.Night}.");
    }

    [Test]
    public void A9_TwoRunsWithTheSameSeedProduceTheSamePlan()
    {
        Metrics.Determinism.RunsIdentical.ShouldBeTrue(
            $"A9: two runs of the same input with seed {Definition.Config.RandomSeed}, evaluation parallelism "
            + $"{Definition.Config.EvaluationParallelism} and no runtime budget must produce the same plan, but they "
            + $"differ: {Run.FirstDifference ?? Metrics.Determinism.FirstDifference}");
    }

    private static IReadOnlyList<string> ValidateFixture(AutofillScenarioDefinition definition, double guaranteedHours)
    {
        var problems = new List<string>();
        var periodDays = definition.PeriodUntil.DayNumber - definition.PeriodFrom.DayNumber + 1;

        if (periodDays != AutofillSpecConstants.PeriodDays)
        {
            problems.Add($"period covers {periodDays} days instead of {AutofillSpecConstants.PeriodDays}");
        }

        if (definition.Context.Shifts.Count != AutofillSpecConstants.TotalRequiredShifts)
        {
            problems.Add(
                $"the period demands {definition.Context.Shifts.Count} shifts instead of "
                + $"{AutofillSpecConstants.TotalRequiredShifts}");
        }

        if (definition.EmployeesInListOrder.Count != AutofillSpecConstants.EmployeeCount)
        {
            problems.Add(
                $"the scenario has {definition.EmployeesInListOrder.Count} employees instead of "
                + $"{AutofillSpecConstants.EmployeeCount}");
        }

        var wrongTarget = definition.Context.Agents
            .Where(a => Math.Abs(a.GuaranteedHours - guaranteedHours) > HoursComparisonEpsilon)
            .Select(a => $"{a.Id}={a.GuaranteedHours}")
            .ToList();
        if (wrongTarget.Count > 0)
        {
            problems.Add(
                $"not every employee carries {guaranteedHours} guaranteed hours: {string.Join(", ", wrongTarget)}");
        }

        var wrongContract = definition.Context.Agents
            .Where(a =>
                a.MaxWorkDays != AutofillSpecConstants.MaxWorkDays
                || a.MinRestDays != AutofillSpecConstants.MinRestDays
                || a.MaxConsecutiveDays != AutofillSpecConstants.MaxConsecutiveDays
                || Math.Abs(a.MinRestHours - AutofillSpecConstants.MinRestHours) > HoursComparisonEpsilon)
            .Select(a =>
                $"{a.Id}: maxWorkDays={a.MaxWorkDays}, minRestDays={a.MinRestDays}, "
                + $"maxConsecutiveDays={a.MaxConsecutiveDays}, minRestHours={a.MinRestHours}")
            .ToList();
        if (wrongContract.Count > 0)
        {
            problems.Add(
                $"the contract values A3 and A5 measure against are not the specified ones (maxWorkDays "
                + $"{AutofillSpecConstants.MaxWorkDays}, minRestDays {AutofillSpecConstants.MinRestDays}, "
                + $"maxConsecutiveDays {AutofillSpecConstants.MaxConsecutiveDays}, minRestHours "
                + $"{AutofillSpecConstants.MinRestHours}): {string.Join("; ", wrongContract)}");
        }

        if (definition.Context.SchedulingMaxConsecutiveDays != AutofillSpecConstants.MaxConsecutiveDays
            || Math.Abs(definition.Context.SchedulingMinPauseHours - AutofillSpecConstants.MinRestHours)
                > HoursComparisonEpsilon)
        {
            problems.Add(
                $"the scheduling defaults of the context do not match the contract: "
                + $"maxConsecutiveDays={definition.Context.SchedulingMaxConsecutiveDays} (expected "
                + $"{AutofillSpecConstants.MaxConsecutiveDays}), "
                + $"minPauseHours={definition.Context.SchedulingMinPauseHours} (expected "
                + $"{AutofillSpecConstants.MinRestHours})");
        }

        if (definition.CarryIns.Count > 0 || definition.Context.BoundaryLockedWorks.Count > 0)
        {
            problems.Add(
                $"the clean start requires an empty previous month, but the fixture carries "
                + $"{definition.CarryIns.Count} carry-in package(s) and "
                + $"{definition.Context.BoundaryLockedWorks.Count} fixed previous-month shift(s)");
        }

        if (definition.Config.RandomSeed != AutofillSpecConstants.RandomSeed
            || definition.Config.EvaluationParallelism != AutofillSpecConstants.EvaluationParallelism
            || definition.Config.MaxRuntime is not null)
        {
            problems.Add(
                $"the run is not pinned for determinism: seed={definition.Config.RandomSeed}, "
                + $"parallelism={definition.Config.EvaluationParallelism}, "
                + $"maxRuntime={definition.Config.MaxRuntime?.ToString() ?? "null"}");
        }

        return problems;
    }

    private static string Join(IReadOnlyList<string> lines)
        => lines.Count == 0 ? "none" : Environment.NewLine + string.Join(Environment.NewLine, lines);

    private static string FormatHours(double hours)
        => hours.ToString(HoursFormat, CultureInfo.InvariantCulture);

    private static string FormatPercent(double share)
        => share.ToString(PercentFormat, CultureInfo.InvariantCulture);

    private static string FormatShare(double share)
        => share.ToString(ShareFormat, CultureInfo.InvariantCulture);

    private static string FormatHistogram(IReadOnlyDictionary<string, int> histogram)
        => "{" + string.Join(", ", histogram.Select(e => $"{e.Key}:{e.Value}")) + "}";

    /// <summary>Share of packages of at most <see cref="AutofillSpecConstants.ShortPackageMaxLength"/> days.</summary>
    /// <param name="metrics">Measurement to read the packages from</param>
    private static double ShortPackageShareOf(AutofillMetrics metrics)
    {
        var packages = metrics.Packages.Items;
        return packages.Count == 0
            ? 0
            : (double)packages.Count(p => p.LengthDays <= AutofillSpecConstants.ShortPackageMaxLength)
                / packages.Count;
    }

    private static void AddSpreadProblem(List<string> problems, AutofillShiftKind kind, int spread)
    {
        if (spread > AutofillSpecConstants.MaxShiftKindSpread)
        {
            problems.Add(
                $"{kind}: spread {spread} exceeds the allowed {AutofillSpecConstants.MaxShiftKindSpread}");
        }
    }

    private List<string> DescribeLengthModeProblem()
    {
        var problems = new List<string>();
        var histogram = Metrics.Packages.LengthHistogram;
        var expectedBucket = AutofillSpecConstants.ExpectedPackageLengthMode.ToString(CultureInfo.InvariantCulture);
        histogram.TryGetValue(expectedBucket, out var expectedCount);

        if (Metrics.Packages.Items.Count == 0)
        {
            problems.Add(
                "package length: the plan contains no package at all, so it cannot peak at "
                + $"{AutofillSpecConstants.ExpectedPackageLengthMode} days");
            return problems;
        }

        var stronger = histogram
            .Where(e => !string.Equals(e.Key, expectedBucket, StringComparison.Ordinal) && e.Value >= expectedCount)
            .Select(e => $"{e.Key} days:{e.Value}")
            .ToList();

        if (stronger.Count > 0)
        {
            problems.Add(
                $"package length: length {expectedBucket} holds {expectedCount} package(s) and is not the single "
                + $"most frequent length; at least as frequent: {string.Join(", ", stronger)}");
        }

        return problems;
    }

    private List<string> DescribeShortPackageProblem()
    {
        var problems = new List<string>();
        var packages = Metrics.Packages.Items;
        if (packages.Count == 0)
        {
            return problems;
        }

        var shortPackages = packages
            .Where(p => p.LengthDays <= AutofillSpecConstants.ShortPackageMaxLength)
            .ToList();
        var share = ShortPackageShareOf(Metrics);
        var limit = AutofillSpecConstants.ShortPackageShareLimit + AutofillSpecConstants.ShortPackageShareTolerance;

        if (share <= limit + ShareComparisonEpsilon)
        {
            return problems;
        }

        var listed = shortPackages
            .Select(p =>
                $"{p.Employee} {p.StartDate.ToString(ShortDateFormat, CultureInfo.InvariantCulture)}"
                + $"..{p.EndDate.ToString(ShortDateFormat, CultureInfo.InvariantCulture)} "
                + $"({p.LengthDays} d, {p.ShiftType})")
            .ToList();

        problems.Add(
            $"short packages: {shortPackages.Count} of {packages.Count} packages are at most "
            + $"{AutofillSpecConstants.ShortPackageMaxLength} days long, a share of {FormatShare(share)}, above the "
            + $"tolerated {FormatShare(limit)}: {string.Join(", ", listed)}");
        return problems;
    }

    private List<string> DescribeOverlongPackages()
    {
        var problems = new List<string>();
        var overlong = Metrics.Packages.Items
            .Where(p => p.LengthDays > AutofillSpecConstants.MaxAllowedPackageLength)
            .Select(p =>
                $"{p.Employee} (rank {Definition.ListRankOf(p.Employee)}) "
                + $"{p.StartDate.ToString(DateFormat, CultureInfo.InvariantCulture)}"
                + $"..{p.EndDate.ToString(DateFormat, CultureInfo.InvariantCulture)}: {p.LengthDays} days")
            .ToList();

        if (overlong.Count > 0)
        {
            problems.Add(
                "package length above the specification maximum of "
                + $"{AutofillSpecConstants.MaxAllowedPackageLength} days: {string.Join(", ", overlong)}");
        }

        return problems;
    }

    private List<string> DescribeMixedPackages()
    {
        var shiftsByEmployee = AutofillPlanAnalyzer.BuildPlannedShifts(Run.Plan, Definition, includeCarryIn: false);
        var lines = new List<string>();

        foreach (var package in Metrics.Packages.Items.Where(p => p.MixedTypes))
        {
            var days = new List<string>();
            if (shiftsByEmployee.TryGetValue(package.Employee, out var shifts))
            {
                days.AddRange(shifts
                    .Where(s => s.Date >= package.StartDate && s.Date <= package.EndDate)
                    .OrderBy(s => s.Date)
                    .ThenBy(s => s.StartAt)
                    .Select(s =>
                        $"{s.Date.ToString(ShortDateFormat, CultureInfo.InvariantCulture)}="
                        + $"{AutofillShiftCatalog.SymbolOf(s.Kind)}"));
            }

            lines.Add(
                $"{package.Employee} (rank {Definition.ListRankOf(package.Employee)}) "
                + $"{package.StartDate.ToString(DateFormat, CultureInfo.InvariantCulture)}"
                + $"..{package.EndDate.ToString(DateFormat, CultureInfo.InvariantCulture)} "
                + $"({package.LengthDays} d): {string.Join(" ", days)}");
        }

        return lines;
    }

    private IReadOnlyList<string> DescribeNonForwardTransitions()
    {
        var packagesByEmployee = Metrics.Packages.Items
            .GroupBy(p => p.Employee, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.StartDate).ToList(), StringComparer.Ordinal);
        var seenPerEmployee = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = new List<string>();

        foreach (var transition in Metrics.Rotation.Transitions)
        {
            seenPerEmployee.TryGetValue(transition.Employee, out var index);
            seenPerEmployee[transition.Employee] = index + 1;

            if (transition.Forward)
            {
                continue;
            }

            var boundary = string.Empty;
            if (packagesByEmployee.TryGetValue(transition.Employee, out var packages) && index + 1 < packages.Count)
            {
                boundary =
                    $" at the boundary {packages[index].EndDate.ToString(DateFormat, CultureInfo.InvariantCulture)}"
                    + $" to {packages[index + 1].StartDate.ToString(DateFormat, CultureInfo.InvariantCulture)}";
            }

            lines.Add(
                $"{transition.Employee} (rank {Definition.ListRankOf(transition.Employee)}): "
                + $"{transition.FromType} to {transition.ToType}{boundary}, forced={transition.Forced}");
        }

        return lines;
    }

    private string FormatHoursPerEmployee()
        => string.Join(
            ", ",
            Metrics.Hours.PerEmployee.Select(e =>
                $"{e.Employee}(rank {e.ListRank})={FormatHours(e.PlannedHours)} h of "
                + $"{FormatHours(e.GuaranteedHours)} h = {FormatPercent(e.FulfillmentPct)}"));

    private string FormatShiftKindCounts(IReadOnlyList<EmployeeShiftTypeCounts> counts)
        => string.Join(
            ", ",
            counts.Select(c => $"{c.Employee}(rank {c.ListRank}) E={c.Early}/L={c.Late}/N={c.Night}"));

    private string FormatFreeBlocks()
        => "{" + string.Join(", ", Metrics.Packages.FreeBlockHistogram.Select(e => $"{e.Key}:{e.Value}")) + "}";

    private void WriteDiagnosis()
    {
        var final = Metrics;
        var seed = Run.SeedMetrics;

        TestContext.Out.WriteLine($"[{ScenarioName}] artifacts:");
        foreach (var path in Run.ArtifactPaths)
        {
            TestContext.Out.WriteLine($"  {path}");
        }

        TestContext.Out.WriteLine(
            $"[{ScenarioName}] final: coverage={final.Coverage.FilledShifts}/{final.Coverage.TotalRequiredShifts}, "
            + $"mixedTypes={final.Packages.MixedTypeCount}/{final.Packages.Items.Count}, "
            + $"idealShare={FormatShare(final.Packages.IdealShare)}, "
            + $"shortPackageShare={FormatShare(ShortPackageShareOf(final))} (limit "
            + $"{FormatShare(AutofillSpecConstants.ShortPackageShareLimit)} + tolerance "
            + $"{FormatShare(AutofillSpecConstants.ShortPackageShareTolerance)}), "
            + $"lengths={FormatHistogram(final.Packages.LengthHistogram)}, "
            + $"forwardRate={FormatShare(final.Rotation.ForwardRate)}, "
            + $"spread E={final.Fairness.SpreadPerType.Early}/L={final.Fairness.SpreadPerType.Late}/"
            + $"N={final.Fairness.SpreadPerType.Night}");
        TestContext.Out.WriteLine($"[{ScenarioName}] final hours: {FormatHoursPerEmployee()}");
        TestContext.Out.WriteLine(
            $"[{ScenarioName}] auction seed plan (diagnosis only, never asserted): "
            + $"coverage={seed.Coverage.FilledShifts}/{seed.Coverage.TotalRequiredShifts}, "
            + $"mixedTypes={seed.Packages.MixedTypeCount}/{seed.Packages.Items.Count}, "
            + $"idealShare={FormatShare(seed.Packages.IdealShare)}, "
            + $"shortPackageShare={FormatShare(ShortPackageShareOf(seed))}, "
            + $"lengths={FormatHistogram(seed.Packages.LengthHistogram)}, "
            + $"forwardRate={FormatShare(seed.Rotation.ForwardRate)}, "
            + $"spread E={seed.Fairness.SpreadPerType.Early}/L={seed.Fairness.SpreadPerType.Late}/"
            + $"N={seed.Fairness.SpreadPerType.Night}");
        TestContext.Out.WriteLine(
            $"[{ScenarioName}] engine fitness (never an oracle): stage0={final.Fitness.Stage0}, "
            + $"stage1={final.Fitness.Stage1}, stage2={final.Fitness.Stage2}, stage3={final.Fitness.Stage3}, "
            + $"stage4={final.Fitness.Stage4}");
    }
}
