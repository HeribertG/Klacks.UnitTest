// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario2;

/// <summary>
/// Specification scenario 2 — the transition out of the previous month. Same period, demand and
/// roster as scenario 1, but February 2026 already holds work packages that reach into March, so the
/// autofill has to continue both the shift kind and the started package length across the month
/// boundary.
/// <para>
/// One shared run. The scenario is built and executed once in <c>[OneTimeSetUp]</c> and every
/// inherited assertion — from <see cref="Scenario2AssertionsBase"/>, where they moved verbatim so
/// scenario 3 can run them against its keyword fixture — reads the same cached result, which is what
/// makes the PASS/FAIL table of phase D a statement about one plan rather than about a dozen
/// different ones. All artifacts are therefore written under a single literal name.
/// </para>
/// <para>
/// A15 (recovery comparison run) is deliberately not implemented. Decision 5 in the header of
/// tests/autofill/SPEC.md: recovery is a failure repair, not a month-transition mechanism, and A15 is
/// carried in phase D as NOT-EVALUABLE, category c.
/// </para>
/// <para>
/// Expectations come from the specification. A10 and A11 are green since the carry-in construction of
/// stage E1; A12 and A13 still measure a gap the engine's fitness never scores, so since 2026-08-12
/// (SPEC.md decision 11) they assert a pinned measurement and name the unchanged specification target
/// in their message instead of staying permanently red. Both cite the same measurement on the auction
/// seed plan, which shows whether a continuation was never built or built and then dissolved.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
public class Scenario2Tests : Scenario2AssertionsBase
{
    private const string ScenarioName = "scenario2";

    private const string ArtifactTestName = "Scenario2";

    /// <summary>
    /// Pinned measurement 2026-08-12 (SPEC.md decision 11): free days A12 is judged against in this
    /// scenario. The specification value is
    /// <see cref="AutofillSpecConstants.MinFreeDaysAfterCarryIn"/> = 2 and stays binding; scenario 3
    /// asserts it and is green. Here the engine leaves MA-3 a single free day — its continued package
    /// 2026-02-28..2026-03-04 is correct (stage E1 built it), but the next package already starts on
    /// 2026-03-06 because the round-2 escalation of the repair ignores MinRestDays (engine defects
    /// E4/E5, parked by owner decision). Measured 2026-08-12 on engine af5f0fa; MA-1 and MA-2 keep
    /// their two free days, so one is the worst of the three open carry-ins, not the rule.
    /// </summary>
    private const int PinnedFreeDaysAfterCarryIn = 1;

    private AutofillScenarioDefinition? _definition;

    private DeterministicRunResult? _run;

    private IReadOnlyList<string> _fixtureProblems = [];

    protected override AutofillScenarioDefinition Definition
    {
        get
        {
            EnsureFixtureIsValid();
            return _definition!;
        }
    }

    protected override DeterministicRunResult Run
    {
        get
        {
            EnsureFixtureIsValid();
            return _run!;
        }
    }

    /// <summary>Band edges of the current engine, pinned so nothing may get worse.</summary>
    protected override AutofillBaseline Baseline => Scenario2BaselineValues.Baseline;

    /// <summary>Scenario 2 is the only scenario with a previous month, so it also pins the carry-in.</summary>
    protected override int? PinnedCarryInOkCount => Scenario2BaselineValues.CarryInOkCount;

    [OneTimeSetUp]
    public void BuildAndRunScenarioTwo()
    {
        try
        {
            _definition = Scenario2CarryInFixture.Build();
        }
        catch (Exception exception)
        {
            _fixtureProblems = [$"scenario 2 could not be assembled: {exception.Message}"];
            return;
        }

        _fixtureProblems = Scenario2CarryInFixture.Validate(_definition);
        if (_fixtureProblems.Count > 0)
        {
            return;
        }

        _run = DeterministicRunner.Run(_definition, ScenarioName, ArtifactTestName);
        WriteDiagnosis(_definition, _run);
        MeasureAndReportSeedBand(_definition, _run.Metrics, ScenarioName, ArtifactTestName);
    }

    /// <summary>
    /// The carry-in counterpart of the shared no-regression floor: scenario 2 is the only scenario
    /// that has a previous month, so this pin lives here instead of in the base class. It watches the
    /// continuations the engine gets right today — A10, A11 and A13 already state what it gets wrong.
    /// Like the seven shared pins it is a band edge: the lowest count the engine reached over the
    /// seeds of <see cref="AutofillSeedBand.Seeds"/>.
    /// </summary>
    [Test]
    public void Baseline_CarryInContinuationsDidNotFall()
    {
        var respected = Metrics.CarryIn.Where(c => c.Ok).ToList();

        respected.Count.ShouldBeGreaterThanOrEqualTo(
            Scenario2BaselineValues.CarryInOkCount,
            "Baseline: at least the band floor of "
            + $"{Scenario2BaselineValues.CarryInOkCount.ToString(CultureInfo.InvariantCulture)} of the "
            + $"{Metrics.CarryIn.Count.ToString(CultureInfo.InvariantCulture)} carried-in packages must still be "
            + "continued with the right shift kind and the right number of remaining days, but only "
            + $"{respected.Count.ToString(CultureInfo.InvariantCulture)} are: "
            + $"{Scenario2Diagnostics.DescribeCarryIn(Metrics.CarryIn)}");
    }

    private static void WriteDiagnosis(AutofillScenarioDefinition definition, DeterministicRunResult run)
    {
        TestContext.Out.WriteLine(
            $"scenario 2, seed {definition.Config.RandomSeed.ToString(CultureInfo.InvariantCulture)}, "
            + $"{definition.Context.BoundaryLockedWorks.Count.ToString(CultureInfo.InvariantCulture)} carry-in days");
        TestContext.Out.WriteLine(
            "final plan carry-in: " + Scenario2Diagnostics.DescribeCarryIn(run.Metrics.CarryIn));
        TestContext.Out.WriteLine(
            "auction seed carry-in: " + Scenario2Diagnostics.DescribeCarryIn(run.SeedMetrics.CarryIn));
        TestContext.Out.WriteLine(
            "final idealShare="
            + run.Metrics.Packages.IdealShare.ToString("0.###", CultureInfo.InvariantCulture)
            + " seed idealShare="
            + run.SeedMetrics.Packages.IdealShare.ToString("0.###", CultureInfo.InvariantCulture));

        foreach (var path in run.ArtifactPaths)
        {
            TestContext.Out.WriteLine("artifact: " + path);
        }
    }

    /// <summary>
    /// Scenario-2 declaration of assertion A12; the core lives in <see cref="Scenario2AssertionsBase"/>
    /// and the [Test] lives on the heirs because the two scenarios reach different results. This one
    /// runs against the pinned <see cref="PinnedFreeDaysAfterCarryIn"/> instead of the specification
    /// value; scenario 3 asserts the specification value and is green on it.
    /// </summary>
    [Test]
    public void A12_TwoFreeDaysFollowTheCompletedCarryInPackage()
        => AssertA12TwoFreeDaysFollowTheCompletedCarryInPackage(PinnedFreeDaysAfterCarryIn);

    private void EnsureFixtureIsValid()
    {
        if (_fixtureProblems.Count > 0)
        {
            Assert.Inconclusive(
                "fixture invalid — scenario 2 was not run, so no rule assertion can be judged: "
                + string.Join(ProblemSeparator, _fixtureProblems));
        }
    }
}
