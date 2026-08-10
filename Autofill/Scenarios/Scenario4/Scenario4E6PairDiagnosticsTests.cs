// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// The measurement stage E6 has to pass before a single line of the Pareto gate is written. Stage E0
/// found that every kind swap which would raise the shift-kind fairness was rejected by the full
/// lexicographic <c>Compare</c>, but it could not say WHY: E0 reconstructed the swap reach and the
/// fairness score from an artifact in Python and stated plainly that the compare gate is not
/// reproducible that way. This fixture answers the missing half with the production evaluator.
/// <para>
/// For every candidate pair of <c>ShiftKindBalancer.FindImprovingSwap:56-105</c> — same first day,
/// same length, different kind — that survives the assignment filter and raises the kind fairness, it
/// prints every component the proposed Pareto gate would look at, before and after:
/// <c>Stage0Legality</c>, <c>Stage0</c>, <c>Stage1</c>, <c>Stage2</c>, <c>BlockOrder</c>,
/// <c>Blacklist</c>, <c>Location</c>, <c>MaxGap</c>, <c>ShiftKindFairness</c> and a count of packages
/// longer than the soft cap. If every pair loses on one of the components the gate protects — above
/// all <c>BlockOrder</c> — then fairness really costs rotation and E6 is dropped; if a pair loses only
/// on <c>Location</c>, <c>MaxGap</c> or a stage-4 helper, the loosening is justified and E6 is built.
/// </para>
/// <para>
/// The component values come from <c>TokenFitnessEvaluator.EvaluateDetailed</c>, which is public and
/// exposes exactly the Stage-3 and Stage-4 breakdown this needs. The plan's step "make
/// <c>ComputeBlockOrderingScore</c> and <c>ComputeBlacklistScore</c> internal" is therefore not needed
/// for the measurement; it stays a question for the production gate, which runs inside a swap loop
/// and cannot afford a full re-evaluation.
/// </para>
/// </summary>
[TestFixture]
[Explicit("E6 pre-measurement; one full scenario-4 run per case, minutes of engine time. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4E6PairDiagnosticsTests
{
    private const string MainRun = "S4a-main-180h";

    private const string CalibrationRun = "S4b-calibration-150h";

    private const string ScoreFormat = "0.000000";

    private const string RuleSeparator = " | ";

    private const string ListSeparator = ", ";

    private const string None = "none";

    private const string BuildE6 = "BUILD E6";

    private const string DropE6 = "DROP E6";

    private const string Unclear = "UNCLEAR";

    private const string Stage0LegalityName = "Stage0Legality";

    private const string Stage0Name = "Stage0";

    private const string Stage1Name = "Stage1";

    private const string Stage2Name = "Stage2";

    private const string BlockOrderName = "BlockOrder";

    private const string BlacklistName = "Blacklist";

    private const string OverlongName = "OverlongPackages";

    /// <summary>
    /// Measures one scenario. Both regimes are offered because the E6 gate of the plan is stated for
    /// both: the spread target is written against the calibration run, the "not worse than today"
    /// clause against the main run.
    /// </summary>
    /// <param name="runLabel">Which fixture of scenario 4 to plan and measure</param>
    [TestCase(MainRun)]
    [TestCase(CalibrationRun)]
    public void TheFairnessImprovingSwapsAreReportedWithEveryCompareComponent(string runLabel)
    {
        var definition = runLabel == CalibrationRun
            ? Scenario4CarryInFixture.BuildCalibrationRun()
            : Scenario4CarryInFixture.BuildMainRun();

        var context = definition.Context;
        var plan = TokenEvolutionLoop.Create().Run(context, definition.Config);
        var evaluator = TokenFitnessEvaluator.Create(context, definition.Config);
        var baseline = evaluator.EvaluateDetailed(plan, context);
        var baselineOverlong = CountOverlongPackages(plan, context);

        var blocksByAgent = new Dictionary<string, List<KindBlock>>(StringComparer.Ordinal);
        foreach (var agent in context.Agents)
        {
            blocksByAgent[agent.Id] = BuildKindBlocks(plan.Tokens, agent.Id, context);
        }

        var strictPairs = 0;
        var filteredOut = 0;
        var withoutFairnessGain = 0;
        var relaxedPairs = 0;
        var reports = new List<PairReport>();

        for (var a = 0; a < context.Agents.Count; a++)
        {
            var agentA = context.Agents[a];
            foreach (var blockA in blocksByAgent[agentA.Id])
            {
                for (var b = a + 1; b < context.Agents.Count; b++)
                {
                    var agentB = context.Agents[b];
                    foreach (var blockB in blocksByAgent[agentB.Id])
                    {
                        if (blockB.Kind == blockA.Kind)
                        {
                            continue;
                        }

                        if (SharesAnyDay(blockA, blockB))
                        {
                            relaxedPairs++;
                        }

                        if (blockB.FirstDay != blockA.FirstDay || blockB.Tokens.Count != blockA.Tokens.Count)
                        {
                            continue;
                        }

                        strictPairs++;
                        var candidate = TrySwap(plan, context, agentA, blockA, agentB, blockB);
                        if (candidate is null)
                        {
                            filteredOut++;
                            continue;
                        }

                        var detailed = evaluator.EvaluateDetailed(candidate, context);
                        if (detailed.Stage4Components.ShiftKindFairness
                            <= baseline.Stage4Components.ShiftKindFairness)
                        {
                            withoutFairnessGain++;
                            continue;
                        }

                        reports.Add(new PairReport(
                            agentA.Id,
                            agentB.Id,
                            blockA.FirstDay,
                            blockA.Tokens.Count,
                            blockA.Kind,
                            blockB.Kind,
                            plan.FitnessStage0Legality,
                            candidate.FitnessStage0Legality,
                            detailed,
                            CountOverlongPackages(candidate, context),
                            evaluator.Compare(candidate, plan)));
                    }
                }
            }
        }

        ReportHeader(runLabel, context, plan, baseline, baselineOverlong);
        ReportPairs(reports, baseline, baselineOverlong);
        ReportVerdict(reports, baseline, baselineOverlong, strictPairs, filteredOut, withoutFairnessGain, relaxedPairs);

        strictPairs.ShouldBeGreaterThan(
            0,
            "the balancer's strict reach must produce candidate pairs on this plan; zero would mean the "
            + "block builder found nothing swappable and the verdict below would say nothing about E6");
    }

    private static void ReportHeader(
        string runLabel,
        CoreWizardContext context,
        CoreScenario plan,
        DetailedFitnessResult baseline,
        int baselineOverlong)
    {
        TestContext.Out.WriteLine(
            "=== " + runLabel
            + ": " + context.Agents.Count.ToString(CultureInfo.InvariantCulture) + " employees, "
            + plan.Tokens.Count.ToString(CultureInfo.InvariantCulture) + " tokens ===");
        TestContext.Out.WriteLine(
            Stage0LegalityName + " " + plan.FitnessStage0Legality.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + Stage0Name + " " + baseline.Stage0.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + Stage1Name + " " + Format(baseline.Stage1)
            + RuleSeparator + Stage2Name + " " + Format(baseline.Stage2)
            + RuleSeparator + BlockOrderName + " " + Format(baseline.Stage3Components.BlockOrder)
            + RuleSeparator + BlacklistName + " " + Format(baseline.Stage3Components.Blacklist)
            + RuleSeparator + "Location " + Format(baseline.Stage3Components.Location)
            + RuleSeparator + "MaxGap " + Format(baseline.Stage3Components.MaxGap)
            + RuleSeparator + "KindFairness " + Format(baseline.Stage4Components.ShiftKindFairness)
            + RuleSeparator + OverlongName + " " + baselineOverlong.ToString(CultureInfo.InvariantCulture));
    }

    private static void ReportPairs(
        IReadOnlyList<PairReport> reports,
        DetailedFitnessResult baseline,
        int baselineOverlong)
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "--- pairs that survive the assignment filter AND raise the kind fairness ---");

        if (reports.Count == 0)
        {
            TestContext.Out.WriteLine(None);
            return;
        }

        foreach (var report in reports)
        {
            TestContext.Out.WriteLine(string.Empty);
            TestContext.Out.WriteLine(
                report.AgentA + " (kind " + report.KindA.ToString(CultureInfo.InvariantCulture) + ")"
                + " <-> " + report.AgentB + " (kind " + report.KindB.ToString(CultureInfo.InvariantCulture) + ")"
                + RuleSeparator + "from "
                + report.FirstDay.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture)
                + " over " + report.Length.ToString(CultureInfo.InvariantCulture) + " day(s)");
            TestContext.Out.WriteLine("component | before | after | direction");

            WriteComponent(Stage0LegalityName, report.LegalityBefore, report.LegalityAfter, lowerIsBetter: true);
            WriteComponent(Stage0Name, baseline.Stage0, report.Detailed.Stage0, lowerIsBetter: true);
            WriteComponent(Stage1Name, baseline.Stage1, report.Detailed.Stage1, lowerIsBetter: false);
            WriteComponent(Stage2Name, baseline.Stage2, report.Detailed.Stage2, lowerIsBetter: false);
            WriteComponent(
                BlockOrderName,
                baseline.Stage3Components.BlockOrder,
                report.Detailed.Stage3Components.BlockOrder,
                lowerIsBetter: false);
            WriteComponent(
                BlacklistName,
                baseline.Stage3Components.Blacklist,
                report.Detailed.Stage3Components.Blacklist,
                lowerIsBetter: false);
            WriteComponent(
                "Location",
                baseline.Stage3Components.Location,
                report.Detailed.Stage3Components.Location,
                lowerIsBetter: false);
            WriteComponent(
                "MaxGap",
                baseline.Stage3Components.MaxGap,
                report.Detailed.Stage3Components.MaxGap,
                lowerIsBetter: false);
            WriteComponent(
                "Fairness",
                baseline.Stage4Components.Fairness,
                report.Detailed.Stage4Components.Fairness,
                lowerIsBetter: false);
            WriteComponent(
                "MinimumHours",
                baseline.Stage4Components.MinimumHours,
                report.Detailed.Stage4Components.MinimumHours,
                lowerIsBetter: false);
            WriteComponent(
                "BlockSymmetry",
                baseline.Stage4Components.BlockSymmetry,
                report.Detailed.Stage4Components.BlockSymmetry,
                lowerIsBetter: false);
            WriteComponent(
                "KindFairness",
                baseline.Stage4Components.ShiftKindFairness,
                report.Detailed.Stage4Components.ShiftKindFairness,
                lowerIsBetter: false);
            WriteComponent(OverlongName, baselineOverlong, report.Overlong, lowerIsBetter: true);
            WriteComponent("Stage3", baseline.Stage3, report.Detailed.Stage3, lowerIsBetter: false);
            WriteComponent("Stage4", baseline.Stage4, report.Detailed.Stage4, lowerIsBetter: false);

            var blockers = HardBlockersOf(report, baseline, baselineOverlong);
            TestContext.Out.WriteLine(
                "today's gate Compare(candidate, plan) = "
                + report.CompareResult.ToString(CultureInfo.InvariantCulture)
                + " (" + (report.CompareResult < 0 ? "accepted today" : "rejected today") + ")");
            TestContext.Out.WriteLine(
                "components the Pareto gate protects and this swap would damage: "
                + (blockers.Count == 0 ? None : string.Join(ListSeparator, blockers)));
        }
    }

    private static void ReportVerdict(
        IReadOnlyList<PairReport> reports,
        DetailedFitnessResult baseline,
        int baselineOverlong,
        int strictPairs,
        int filteredOut,
        int withoutFairnessGain,
        int relaxedPairs)
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "strict candidate pairs " + strictPairs.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + "rejected by the assignment filter " + filteredOut.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + "legal but no fairness gain " + withoutFairnessGain.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + "reach the gate " + reports.Count.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + "pairs under a day-intersection reach, strict ones included "
            + relaxedPairs.ToString(CultureInfo.InvariantCulture));
        TestContext.Out.WriteLine(
            "The day-intersection number counts the same kind-pure blocks the production balancer builds "
            + "and is NOT comparable with the 631 of stage E0, which came from a block builder written in "
            + "Python over the artifact matrix. Compare it only against itself across runs.");

        var acceptedToday = reports.Count(report => report.CompareResult < 0);
        var blockedBy = new Dictionary<string, int>(StringComparer.Ordinal);
        var paretoAccepted = 0;

        foreach (var report in reports)
        {
            var blockers = HardBlockersOf(report, baseline, baselineOverlong);
            if (blockers.Count == 0)
            {
                paretoAccepted++;
                continue;
            }

            foreach (var blocker in blockers)
            {
                blockedBy[blocker] = blockedBy.GetValueOrDefault(blocker, 0) + 1;
            }
        }

        TestContext.Out.WriteLine(
            "accepted by today's lexicographic gate " + acceptedToday.ToString(CultureInfo.InvariantCulture)
            + RuleSeparator + "accepted by the proposed Pareto gate "
            + paretoAccepted.ToString(CultureInfo.InvariantCulture));
        TestContext.Out.WriteLine(
            "blocked by: " + (blockedBy.Count == 0
                ? None
                : string.Join(ListSeparator, blockedBy
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Key + " " + entry.Value.ToString(CultureInfo.InvariantCulture)))));

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("E6 VERDICT: " + VerdictOf(reports.Count, paretoAccepted, blockedBy));
    }

    private static string VerdictOf(int reachedGate, int paretoAccepted, IReadOnlyDictionary<string, int> blockedBy)
    {
        if (reachedGate == 0)
        {
            return Unclear
                + " — not one legal swap on this plan raises the kind fairness, so the gate was never the "
                + "binding constraint here. Either the reach is too narrow (that is the day-intersection "
                + "loosening, step 3 of the plan) or this plan is already at the reachable fairness.";
        }

        if (paretoAccepted == 0)
        {
            var blockOrderShare = blockedBy.GetValueOrDefault(BlockOrderName, 0);
            return DropE6
                + " — every one of the " + reachedGate.ToString(CultureInfo.InvariantCulture)
                + " fairness-improving legal swaps damages a component the Pareto gate protects"
                + (blockOrderShare > 0
                    ? ", " + blockOrderShare.ToString(CultureInfo.InvariantCulture) + " of them " + BlockOrderName
                    : string.Empty)
                + ". The fairness really costs rotation or package constancy, and the spread lever lies "
                + "entirely in E4.";
        }

        return BuildE6
            + " — " + paretoAccepted.ToString(CultureInfo.InvariantCulture) + " of "
            + reachedGate.ToString(CultureInfo.InvariantCulture)
            + " fairness-improving legal swaps damage nothing the priority order protects and are rejected "
            + "today only by an unnumbered helper term. The loosening is justified.";
    }

    /// <summary>
    /// The components of the proposed Pareto gate (plan section 5.2) this swap would move the wrong
    /// way. An empty list means the gate would accept it.
    /// </summary>
    private static List<string> HardBlockersOf(
        PairReport report, DetailedFitnessResult baseline, int baselineOverlong)
    {
        var blockers = new List<string>();

        if (report.LegalityAfter > report.LegalityBefore)
        {
            blockers.Add(Stage0LegalityName);
        }

        if (report.Detailed.Stage0 > baseline.Stage0)
        {
            blockers.Add(Stage0Name);
        }

        if (report.Detailed.Stage1 < baseline.Stage1)
        {
            blockers.Add(Stage1Name);
        }

        if (report.Detailed.Stage2 < baseline.Stage2)
        {
            blockers.Add(Stage2Name);
        }

        if (report.Detailed.Stage3Components.BlockOrder < baseline.Stage3Components.BlockOrder)
        {
            blockers.Add(BlockOrderName);
        }

        if (report.Detailed.Stage3Components.Blacklist < baseline.Stage3Components.Blacklist)
        {
            blockers.Add(BlacklistName);
        }

        if (report.Overlong > baselineOverlong)
        {
            blockers.Add(OverlongName);
        }

        return blockers;
    }

    private static void WriteComponent(string name, double before, double after, bool lowerIsBetter)
    {
        var better = lowerIsBetter ? after < before : after > before;
        var worse = lowerIsBetter ? after > before : after < before;
        TestContext.Out.WriteLine(
            name
            + RuleSeparator + Format(before)
            + RuleSeparator + Format(after)
            + RuleSeparator + (better ? "better" : worse ? "WORSE" : "unchanged"));
    }

    /// <summary>
    /// Packages longer than the soft cap. Rule 6 has no fitness representation, so the Pareto gate
    /// needs this counter explicitly (plan section 5.2, detail 2). Under the strict reach both
    /// employees keep exactly the same worked days, so the value must stay unchanged — a change here
    /// is a defect in the swap replica, not a property of the swap.
    /// </summary>
    private static int CountOverlongPackages(CoreScenario scenario, CoreWizardContext context)
    {
        var overlong = 0;

        foreach (var agent in context.Agents)
        {
            var cap = agent.MaxWorkDays > 0 ? agent.MaxWorkDays : AutofillSpecConstants.MaxWorkDays;
            if (cap <= 0)
            {
                continue;
            }

            var days = scenario.Tokens
                .Where(token => string.Equals(token.AgentId, agent.Id, StringComparison.Ordinal))
                .Select(token => token.Date)
                .Distinct()
                .OrderBy(date => date)
                .ToList();

            var run = 0;
            for (var i = 0; i < days.Count; i++)
            {
                run = i > 0 && days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;
                if (run == cap + 1)
                {
                    overlong++;
                }
            }
        }

        return overlong;
    }

    private static bool SharesAnyDay(KindBlock blockA, KindBlock blockB)
    {
        var days = blockA.Tokens.Select(token => token.Date).ToHashSet();
        return blockB.Tokens.Any(token => days.Contains(token.Date));
    }

    /// <summary>
    /// Replica of <c>ShiftKindBalancer.TrySwap:112-145</c>. <c>TokenSwapMutation.CloneScenario:149</c>
    /// is internal to the engine assembly, so the two lines it contains are reproduced here; every
    /// other step calls the production code.
    /// </summary>
    private static CoreScenario? TrySwap(
        CoreScenario scenario,
        CoreWizardContext context,
        CoreAgent agentA,
        KindBlock blockA,
        CoreAgent agentB,
        KindBlock blockB)
    {
        var removed = new HashSet<CoreToken>(blockA.Tokens);
        removed.UnionWith(blockB.Tokens);
        var working = scenario.Tokens.Where(token => !removed.Contains(token)).ToList();

        foreach (var (token, receiver) in blockB.Tokens.Select(t => (t, agentA))
                     .Concat(blockA.Tokens.Select(t => (t, agentB)))
                     .OrderBy(p => p.t.Date)
                     .ThenBy(p => p.t.StartAt))
        {
            if (!SlotConstraintFilter.IsValidAssignment(
                    receiver, token.Date, token.ShiftTypeIndex, token.ShiftRefId,
                    token.TotalHours, context, working, token.StartAt, token.EndAt))
            {
                return null;
            }

            working.Add(token with
            {
                AgentId = receiver.Id,
                Surcharges = SurchargeEstimator.Estimate(
                    token.TotalHours, token.ShiftTypeIndex, token.Date, receiver),
            });
        }

        return new CoreScenario
        {
            Id = Guid.NewGuid().ToString(),
            Tokens = working,
        };
    }

    /// <summary>Replica of <c>ShiftKindBalancer.BuildKindBlocks:152-222</c>, walked in the same order.</summary>
    private static List<KindBlock> BuildKindBlocks(
        IReadOnlyList<CoreToken> tokens, string agentId, CoreWizardContext context)
    {
        var boundaryDays = new HashSet<DateOnly>();
        foreach (var locked in context.BoundaryLockedWorks)
        {
            if (string.Equals(locked.AgentId, agentId, StringComparison.Ordinal))
            {
                boundaryDays.Add(locked.Date);
            }
        }

        foreach (var blocker in context.BoundaryExistingWorkBlockers)
        {
            if (string.Equals(blocker.AgentId, agentId, StringComparison.Ordinal))
            {
                boundaryDays.Add(blocker.Date);
            }
        }

        var own = tokens
            .Where(token => string.Equals(token.AgentId, agentId, StringComparison.Ordinal))
            .OrderBy(token => token.Date)
            .ThenBy(token => token.StartAt)
            .ToList();

        var blocks = new List<KindBlock>();
        var run = new List<CoreToken>();

        void CloseRun()
        {
            if (run.Count > 0
                && run.All(token => !token.IsLocked)
                && !boundaryDays.Contains(run[0].Date.AddDays(-1)))
            {
                blocks.Add(new KindBlock(run[0].ShiftTypeIndex, run[0].Date, run.ToList()));
            }

            run.Clear();
        }

        for (var i = 0; i < own.Count; i++)
        {
            var token = own[i];
            var continues = run.Count > 0
                && token.Date == run[^1].Date.AddDays(1)
                && token.ShiftTypeIndex == run[0].ShiftTypeIndex;
            var sameDay = run.Count > 0 && token.Date == run[^1].Date;

            if (sameDay)
            {
                run.Clear();
                while (i + 1 < own.Count && own[i + 1].Date == token.Date)
                {
                    i++;
                }

                continue;
            }

            if (!continues)
            {
                CloseRun();
            }

            run.Add(token);
        }

        CloseRun();
        return blocks;
    }

    private static string Format(double value) => value.ToString(ScoreFormat, CultureInfo.InvariantCulture);

    /// <param name="Kind">Shift type every token of the block carries</param>
    /// <param name="FirstDay">First calendar day of the block</param>
    /// <param name="Tokens">Tokens of the block, one per day, in day order</param>
    private sealed record KindBlock(int Kind, DateOnly FirstDay, List<CoreToken> Tokens);

    /// <param name="AgentA">Employee on the low roster side of the pair</param>
    /// <param name="AgentB">Employee on the high roster side of the pair</param>
    /// <param name="FirstDay">First day both blocks cover</param>
    /// <param name="Length">Number of days both blocks cover</param>
    /// <param name="KindA">Shift kind employee A holds before the swap</param>
    /// <param name="KindB">Shift kind employee B holds before the swap</param>
    /// <param name="LegalityBefore">Stage-0 legality count of the plan</param>
    /// <param name="LegalityAfter">Stage-0 legality count after the swap</param>
    /// <param name="Detailed">Full fitness decomposition of the swapped plan</param>
    /// <param name="Overlong">Packages longer than the soft cap after the swap</param>
    /// <param name="CompareResult">Verdict of today's lexicographic gate; negative means accepted</param>
    private sealed record PairReport(
        string AgentA,
        string AgentB,
        DateOnly FirstDay,
        int Length,
        int KindA,
        int KindB,
        int LegalityBefore,
        int LegalityAfter,
        DetailedFitnessResult Detailed,
        int Overlong,
        int CompareResult);
}
