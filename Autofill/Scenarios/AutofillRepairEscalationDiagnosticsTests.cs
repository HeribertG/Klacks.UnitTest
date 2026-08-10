// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Scenarios.Scenario2;
using Klacks.UnitTest.Autofill.Scenarios.Scenario3;
using Klacks.UnitTest.Autofill.Scenarios.Scenario4;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios;

/// <summary>
/// Counts how often the widest rung of the repair operator's coverage escalation actually staffs a
/// slot. The rung is consulted on every slot that no strict candidate and no relocation can take —
/// in a scenario with permanently unstaffable demand that is every sweep of every generation — but it
/// only changes a plan when it succeeds, and only then does the sink fire. A run without a single
/// line is therefore proof that the ladder left the plan of that fixture exactly as it was.
/// <para>
/// Diagnosis only, never asserted, and read-only with respect to the run: the sink draws no
/// randomness and changes no plan, so the traced run is the plan the guards measure.
/// </para>
/// </summary>
[TestFixture]
[Category("Autofill")]
[NonParallelizable]
[Explicit("Diagnosis: counts the fills that only the widest rung of the coverage escalation made possible")]
public sealed class AutofillRepairEscalationDiagnosticsTests
{
    private const string Scenario1Name = "scenario1";
    private const string Scenario1bName = "scenario1b";
    private const string Scenario2Name = "scenario2";
    private const string Scenario3L0Name = "scenario3-L0";
    private const string Scenario3L1Name = "scenario3-L1";
    private const string Scenario3L4Name = "scenario3-L4";
    private const string Scenario3L5Name = "scenario3-L5";
    private const string Scenario4aName = "scenario4a";
    private const string Scenario4bName = "scenario4b";
    private const string Scenario4cName = "scenario4c";
    private const string Scenario4dName = "scenario4d";
    private const string Scenario4eName = "scenario4e";

    private const string TracePrefix = "ESCALATED ";

    private const int MaxRawLines = 40;

    private const int OwnSeed = 0;

    [TestCase(Scenario1Name, 42)]
    [TestCase(Scenario1Name, 43)]
    [TestCase(Scenario1Name, 44)]
    [TestCase(Scenario1bName, 42)]
    [TestCase(Scenario1bName, 43)]
    [TestCase(Scenario1bName, 44)]
    [TestCase(Scenario2Name, 42)]
    [TestCase(Scenario2Name, 43)]
    [TestCase(Scenario2Name, 44)]
    [TestCase(Scenario3L0Name, OwnSeed)]
    [TestCase(Scenario3L1Name, OwnSeed)]
    [TestCase(Scenario3L4Name, OwnSeed)]
    [TestCase(Scenario3L5Name, OwnSeed)]
    [TestCase(Scenario4aName, OwnSeed)]
    [TestCase(Scenario4bName, OwnSeed)]
    [TestCase(Scenario4cName, OwnSeed)]
    [TestCase(Scenario4dName, OwnSeed)]
    [TestCase(Scenario4eName, OwnSeed)]
    public void EscalatedFillsAreCountedAndAttributed(string scenario, int seed)
    {
        var definition = Build(scenario);
        var config = seed == OwnSeed ? definition.Config : definition.Config with { RandomSeed = seed };

        var trace = new List<string>();
        var started = DateTime.UtcNow;
        var plan = TokenEvolutionLoop.Create().Run(
            definition.Context, config, repairEscalations: trace.Add);
        var elapsed = DateTime.UtcNow - started;

        TestContext.Out.WriteLine($"=== {scenario} seed {config.RandomSeed.ToString(CultureInfo.InvariantCulture)} ===");
        TestContext.Out.WriteLine(
            $"tokens in the final plan: {plan.Tokens.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"slots: {definition.Context.Shifts.Count.ToString(CultureInfo.InvariantCulture)}, "
            + $"wall clock: {elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
        TestContext.Out.WriteLine(
            $"ESCALATION COUNT: {trace.Count.ToString(CultureInfo.InvariantCulture)}");

        if (trace.Count == 0)
        {
            TestContext.Out.WriteLine("the widest rung never staffed a slot in this run");
            return;
        }

        TestContext.Out.WriteLine("--- escalated fills per step (step | count) ---");
        foreach (var group in trace
            .GroupBy(StepKind)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            TestContext.Out.WriteLine(
                $"  {group.Key} | {group.Count().ToString(CultureInfo.InvariantCulture)}");
        }

        TestContext.Out.WriteLine($"--- first {MaxRawLines.ToString(CultureInfo.InvariantCulture)} raw lines ---");
        foreach (var line in trace.Take(MaxRawLines))
        {
            TestContext.Out.WriteLine("  " + line);
        }
    }

    private static AutofillScenarioDefinition Build(string scenario) => scenario switch
    {
        Scenario1Name => CleanStart(AutofillSpecConstants.GuaranteedHours),
        Scenario1bName => CleanStart(AutofillSpecConstants.CalibrationGuaranteedHours),
        Scenario2Name => Scenario2CarryInFixture.Build(),
        Scenario3L0Name => Scenario3EligibilityFixture.BuildL0(),
        Scenario3L1Name => Scenario3EligibilityFixture.BuildL1(),
        Scenario3L4Name => Scenario3EligibilityFixture.BuildL4(),
        Scenario3L5Name => Scenario3EligibilityFixture.BuildL5(),
        Scenario4aName => Scenario4CarryInFixture.BuildMainRun(),
        Scenario4bName => Scenario4CarryInFixture.BuildCalibrationRun(),
        Scenario4cName => Scenario4CarryInFixture.BuildSymmetryRun(),
        Scenario4dName => Scenario4CarryInFixture.BuildWithoutCarryIn(),
        Scenario4eName => ReplanRun(),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown scenario name."),
    };

    /// <summary>
    /// The replanning run of scenario 4: plan the main run first, freeze everything before the replan
    /// date and hand that head to a second run, exactly as the wizard's ReplanFrom does.
    /// </summary>
    private static AutofillScenarioDefinition ReplanRun()
    {
        var baseDefinition = Scenario4CarryInFixture.BuildMainRun();
        var basePlan = TokenEvolutionLoop.Create().Run(baseDefinition.Context, baseDefinition.Config);
        return Scenario4CarryInFixture.BuildReplanRun(
            FrozenPrefix.BuildLockedWorks(basePlan, Scenario4SpecConstants.ReplanFrom));
    }

    private static AutofillScenarioDefinition CleanStart(double guaranteedHours)
        => new AutofillScenarioBuilder()
            .WithPeriod(AutofillSpecConstants.PeriodFrom, AutofillSpecConstants.PeriodUntil)
            .WithEmployees(AutofillSpecConstants.EmployeeCount, guaranteedHours)
            .WithRandomSeed(AutofillSpecConstants.RandomSeed)
            .WithGaParameters(AutofillSpecConstants.PopulationSize, AutofillSpecConstants.MaxGenerations)
            .Build();

    private static string StepKind(string traceLine)
    {
        var body = traceLine.StartsWith(TracePrefix, StringComparison.Ordinal)
            ? traceLine[TracePrefix.Length..]
            : traceLine;
        var separator = body.IndexOf(' ', StringComparison.Ordinal);
        var stage = separator < 0 ? body : body[..separator];
        var dot = stage.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? stage : stage[(dot + 1)..];
    }
}
