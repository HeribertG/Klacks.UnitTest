// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Operators;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario3;

/// <summary>
/// Diagnosis of the L5/L1 isolation breach: which engine channel can carry a restriction that lives
/// entirely on 2026-03-21..03-31 backwards into the days before it. The auction is strictly
/// chronological, so the seed plan cannot leak; every candidate channel therefore sits in the fitness
/// function or in an evolution pass that reads a period-wide aggregate. The three probes below are
/// counterfactual by construction: they feed ONE plan to TWO contexts that differ only in the eleven
/// ban triples, so any difference they report is retroactive by definition and not search noise.
/// </summary>
[TestFixture]
[Explicit("Isolation channel diagnosis; reports numbers and asserts only its own replay. Select it by name.")]
[Category("Autofill")]
[Category("Scenario3")]
public class Scenario3IsolationChannelDiagnosticsTests
{
    private const string ReportFileName = "Scenario3.isolation-channel.txt";

    private static readonly DateOnly Cutoff = Scenario3SpecValues.Ma2NightBanFrom;

    /// <summary>The seeds the no-regression band is measured over.</summary>
    private static readonly int[] BandSeeds = [42, 43, 44];

    private AutofillScenarioDefinition _l1 = null!;

    private AutofillScenarioDefinition _l5 = null!;

    private readonly StringBuilder _report = new();

    [OneTimeSetUp]
    public void Build()
    {
        _l1 = Scenario3EligibilityFixture.BuildL1();
        _l5 = Scenario3EligibilityFixture.BuildL5();
    }

    [OneTimeTearDown]
    public void WriteReport()
    {
        var path = AutofillRepositoryPaths.ArtifactDirectory(Scenario3SpecValues.ScenarioName);
        File.WriteAllText(Path.Combine(path, ReportFileName), _report.ToString());
        TestContext.Out.WriteLine(_report.ToString());
    }

    /// <summary>
    /// Probe 1 — the fitness channel. One plan, two contexts: every stage and every stage-3/stage-4
    /// component that moves is a channel through which the post-cutoff ban re-scores pre-cutoff work.
    /// </summary>
    [Test]
    public void Probe1_WhichFitnessComponentMovesWhenOnlyTheBanListChanges()
    {
        var seed = AutofillSeedPlanFactory.BuildAuctionSeedPlan(_l1);
        var evaluatorL1 = TokenFitnessEvaluator.Create(_l1.Context, _l1.Config);
        var evaluatorL5 = TokenFitnessEvaluator.Create(_l5.Context, _l5.Config);

        var underL1 = evaluatorL1.EvaluateDetailed(Clone(seed), _l1.Context);
        var underL5 = evaluatorL5.EvaluateDetailed(Clone(seed), _l5.Context);

        Line("PROBE 1 — same plan (L1 auction seed), two contexts");
        Line($"  Stage0            {underL1.Stage0} | {underL5.Stage0}");
        Line($"  Stage1            {Fmt(underL1.Stage1)} | {Fmt(underL5.Stage1)}");
        Line($"  Stage2            {Fmt(underL1.Stage2)} | {Fmt(underL5.Stage2)}");
        Line($"  Stage3            {Fmt(underL1.Stage3)} | {Fmt(underL5.Stage3)}");
        Line($"  Stage4            {Fmt(underL1.Stage4)} | {Fmt(underL5.Stage4)}");
        Line($"  S3.BlockOrder     {Fmt(underL1.Stage3Components.BlockOrder)} | {Fmt(underL5.Stage3Components.BlockOrder)}");
        Line($"  S3.Blacklist      {Fmt(underL1.Stage3Components.Blacklist)} | {Fmt(underL5.Stage3Components.Blacklist)}");
        Line($"  S3.Location       {Fmt(underL1.Stage3Components.Location)} | {Fmt(underL5.Stage3Components.Location)}");
        Line($"  S3.MaxGap         {Fmt(underL1.Stage3Components.MaxGap)} | {Fmt(underL5.Stage3Components.MaxGap)}");
        Line($"  S4.Fairness       {Fmt(underL1.Stage4Components.Fairness)} | {Fmt(underL5.Stage4Components.Fairness)}");
        Line($"  S4.MinimumHours   {Fmt(underL1.Stage4Components.MinimumHours)} | {Fmt(underL5.Stage4Components.MinimumHours)}");
        Line($"  S4.BlockSymmetry  {Fmt(underL1.Stage4Components.BlockSymmetry)} | {Fmt(underL5.Stage4Components.BlockSymmetry)}");
        Line($"  S4.KindFairness   {Fmt(underL1.Stage4Components.ShiftKindFairness)} | {Fmt(underL5.Stage4Components.ShiftKindFairness)}");
        Line(string.Empty);

        Assert.Pass();
    }

    /// <summary>
    /// Probe 2 — the operator channel. Each period-wide pass is applied to the SAME input plan under
    /// both contexts; a pre-cutoff difference in the output is a retroactive rewrite of days the
    /// restriction does not touch.
    /// </summary>
    [Test]
    public void Probe2_WhichEvolutionPassRewritesPreCutoffDaysWhenOnlyTheBanListChanges()
    {
        var seed = AutofillSeedPlanFactory.BuildAuctionSeedPlan(_l1);
        var evaluatorL1 = TokenFitnessEvaluator.Create(_l1.Context, _l1.Config);
        var evaluatorL5 = TokenFitnessEvaluator.Create(_l5.Context, _l5.Config);

        Line("PROBE 2 — same input plan (L1 auction seed), each pass run under both contexts");
        ReportPass(
            "TopDownHandover",
            new TopDownHandover().Apply(Clone(seed), _l1.Context),
            new TopDownHandover().Apply(Clone(seed), _l5.Context));
        ReportPass(
            "SurplusHoursReturn",
            new SurplusHoursReturn().Apply(Clone(seed), _l1.Context, evaluatorL1),
            new SurplusHoursReturn().Apply(Clone(seed), _l5.Context, evaluatorL5));
        ReportPass(
            "ShiftKindBalancer",
            new ShiftKindBalancer().Apply(Clone(seed), _l1.Context, evaluatorL1),
            new ShiftKindBalancer().Apply(Clone(seed), _l5.Context, evaluatorL5));
        ReportPass(
            "ObjectContinuityBalancer",
            new ObjectContinuityBalancer().Apply(Clone(seed), _l1.Context, evaluatorL1),
            new ObjectContinuityBalancer().Apply(Clone(seed), _l5.Context, evaluatorL5));
        Line(string.Empty);

        Assert.Pass();
    }

    /// <summary>
    /// Probe 3 — the eligible-day denominators the kind-fairness term normalises by. These are the
    /// numbers that make one and the same pre-cutoff night worth a different share in the two runs.
    /// </summary>
    [Test]
    public void Probe3_KindFairnessDenominatorsDifferForThePreCutoffDays()
    {
        var seed = AutofillSeedPlanFactory.BuildAuctionSeedPlan(_l1);
        var preCutoff = new CoreScenario
        {
            Id = seed.Id,
            Tokens = seed.Tokens.Where(t => t.Date < Cutoff).ToList(),
        };

        var evaluatorL1 = TokenFitnessEvaluator.Create(_l1.Context, _l1.Config);
        var evaluatorL5 = TokenFitnessEvaluator.Create(_l5.Context, _l5.Config);

        Line("PROBE 3 — a plan that contains ONLY pre-cutoff tokens, scored under both contexts");
        Line($"  tokens                 {preCutoff.Tokens.Count.ToString(CultureInfo.InvariantCulture)}");
        var underL1 = evaluatorL1.EvaluateDetailed(Clone(preCutoff), _l1.Context);
        var underL5 = evaluatorL5.EvaluateDetailed(Clone(preCutoff), _l5.Context);
        Line($"  KindFairness under L1  {Fmt(underL1.Stage4Components.ShiftKindFairness)}");
        Line($"  KindFairness under L5  {Fmt(underL5.Stage4Components.ShiftKindFairness)}");
        Line($"  Stage4 under L1        {Fmt(underL1.Stage4)}");
        Line($"  Stage4 under L5        {Fmt(underL5.Stage4)}");
        Line(string.Empty);

        Assert.Pass();
    }

    /// <summary>
    /// Probe 4 — is the coincidence L1 == L5 a property of the engine or an accident of one seed?
    /// The isolation assertion can only be green while the restriction changes nothing. Running the
    /// pair under several seeds answers whether that state is robust or a knife edge.
    /// </summary>
    [Test]
    public void Probe4_DoesTheL1L5CoincidenceSurviveASeedChange()
    {
        Line("PROBE 4 — L1 against L5 per random seed (full token evolution, one run each)");
        foreach (var seed in BandSeeds)
        {
            var l1 = _l1 with { Config = _l1.Config with { RandomSeed = seed } };
            var l5 = _l5 with { Config = _l5.Config with { RandomSeed = seed } };

            var planL1 = TokenEvolutionLoop.Create().Run(l1.Context, l1.Config);
            var planL5 = TokenEvolutionLoop.Create().Run(l5.Context, l5.Config);
            var cells = Differences(planL1, planL5);
            var before = cells.Count(c => c.Date < Cutoff);

            Line($"  seed {seed.ToString(CultureInfo.InvariantCulture)}   assignment diffs {cells.Count,3}"
                + $"   pre-cutoff {before,3}   {(cells.Count == 0 ? "L1 == L5, restriction inert" : "restriction bites")}");
        }

        Line(string.Empty);
        Assert.Pass();
    }

    private void ReportPass(string name, CoreScenario underL1, CoreScenario underL5)
    {
        var before = 0;
        var after = 0;
        var cells = Differences(underL1, underL5);
        foreach (var cell in cells)
        {
            if (cell.Date < Cutoff)
            {
                before++;
            }
            else
            {
                after++;
            }
        }

        Line($"  {name,-26} diffs total {cells.Count,3}   pre-cutoff {before,3}   post-cutoff {after,3}");
        foreach (var cell in cells.Where(c => c.Date < Cutoff))
        {
            Line($"      PRE-CUTOFF {cell.Date:yyyy-MM-dd} {cell.AgentId} kind {cell.Kind.ToString(CultureInfo.InvariantCulture)} {cell.Side}");
        }
    }

    private static IReadOnlyList<(DateOnly Date, string AgentId, int Kind, string Side)> Differences(
        CoreScenario a, CoreScenario b)
    {
        var setA = a.Tokens.Select(Key).ToHashSet();
        var setB = b.Tokens.Select(Key).ToHashSet();
        var result = new List<(DateOnly, string, int, string)>();
        foreach (var key in setA.Except(setB))
        {
            result.Add((key.Date, key.AgentId, key.Kind, "only in L1 context"));
        }

        foreach (var key in setB.Except(setA))
        {
            result.Add((key.Date, key.AgentId, key.Kind, "only in L5 context"));
        }

        return result.OrderBy(r => r.Item1).ThenBy(r => r.Item2, StringComparer.Ordinal).ToList();
    }

    private static (DateOnly Date, string AgentId, int Kind) Key(CoreToken token)
        => (token.Date, token.AgentId, token.ShiftTypeIndex);

    private static CoreScenario Clone(CoreScenario scenario)
        => new() { Id = scenario.Id, Tokens = scenario.Tokens.ToList() };

    private static string Fmt(double value)
        => value.ToString("0.########", CultureInfo.InvariantCulture);

    private void Line(string text) => _report.AppendLine(text);
}
