// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using System.Globalization;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Measures what one scenario-4 engine run costs, so the size of the genetic algorithm is a decision
/// with a number behind it instead of an estimate. Times ONE <c>TokenEvolutionLoop.Run</c>, not a pass
/// of the deterministic runner: the budget of the decision rule is stated per engine run, and the
/// runner executes three of them.
/// <para>
/// The reference run of scenario 2 is measured in the same test on the same machine. The figure the
/// suite documentation carries predates the operator-filter change of P2 and would make the ratio look
/// better than it is.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Timing probe; runs the unmodified GA size on 279 slots. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4TimingProbeTests
{
    private const int MillisecondsPerSecond = 1000;

    [Test]
    public void OneRunAtTheSuiteGaSizeIsTimedAgainstScenarioTwo()
    {
        var reference = Scenario2CarryInFixture.Build();
        var referenceWatch = Stopwatch.StartNew();
        TokenEvolutionLoop.Create().Run(reference.Context, reference.Config);
        referenceWatch.Stop();

        var probe = Scenario4CarryInFixture.BuildTimingProbe();
        var probeWatch = Stopwatch.StartNew();
        var plan = TokenEvolutionLoop.Create().Run(probe.Context, probe.Config);
        probeWatch.Stop();

        var ratio = referenceWatch.ElapsedMilliseconds <= 0
            ? 0
            : probeWatch.ElapsedMilliseconds / (double)referenceWatch.ElapsedMilliseconds;

        TestContext.Out.WriteLine(
            "scenario 2 reference: "
            + $"{referenceWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms for "
            + $"{reference.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)} slots and "
            + $"{reference.Context.Agents.Count.ToString(CultureInfo.InvariantCulture)} agents at population "
            + $"{reference.Config.PopulationSize.ToString(CultureInfo.InvariantCulture)} / "
            + $"{reference.Config.MaxGenerations.ToString(CultureInfo.InvariantCulture)} generations");
        TestContext.Out.WriteLine(
            "scenario 4 probe: "
            + $"{probeWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms ("
            + $"{(probeWatch.ElapsedMilliseconds / (double)MillisecondsPerSecond).ToString("0.#", CultureInfo.InvariantCulture)} s) "
            + $"for {probe.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)} slots and "
            + $"{probe.Context.Agents.Count.ToString(CultureInfo.InvariantCulture)} agents at population "
            + $"{probe.Config.PopulationSize.ToString(CultureInfo.InvariantCulture)} / "
            + $"{probe.Config.MaxGenerations.ToString(CultureInfo.InvariantCulture)} generations");
        TestContext.Out.WriteLine(
            $"runtime.ratioVsScenario2 = {ratio.ToString("0.##", CultureInfo.InvariantCulture)}");
        TestContext.Out.WriteLine(
            "decision rule: at or below 120 s per engine run the scenario-4 runs keep the suite GA size, clearly above "
            + "it they need a reduced one. Probe of 2026-08-09: "
            + $"{Scenario4GaParameters.MeasuredScenario4RunMs.ToString(CultureInfo.InvariantCulture)} ms against "
            + $"{Scenario4GaParameters.MeasuredReferenceRunMs.ToString(CultureInfo.InvariantCulture)} ms, so the runs "
            + $"kept population {Scenario4GaParameters.PopulationSize.ToString(CultureInfo.InvariantCulture)} / "
            + $"{Scenario4GaParameters.MaxGenerations.ToString(CultureInfo.InvariantCulture)}.");

        plan.Tokens.Count.ShouldBeGreaterThan(
            0, "the probe must produce a plan; a run that assigns nothing has not been timed meaningfully");
    }
}
