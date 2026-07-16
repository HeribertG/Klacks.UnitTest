// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Diagnostics;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Multi-seed efficacy benchmark for the Wizard-1 warm-start (Delta 6 of the warm-start handoff).
/// Measures a COLD arm (no WarmStartAssignments) against a WARM arm (assignments derived from a
/// prior cold run, InitWarmStartRatio default 0.2) over a fixed set of RandomSeed values. GA runs
/// are stochastic — never judge a single run; this suite aggregates mean and median per arm.
/// The whole class is [Explicit]; it never runs in the normal suite.
/// </summary>
/// <remarks>
/// CRITICAL fixture detail: CoreShift.Id must be a parseable Guid string. Tokens copy their
/// ShiftRefId from Guid.TryParse(slot.Id) (RandomTokenStrategy.cs:34) and WarmStartTokenStrategy
/// only keeps seed cells whose ShiftRefId is in context.Shifts (Guid-parsed). With non-Guid shift
/// ids every warm seed cell is dropped and the "warm" arm degenerates into a second cold arm.
/// Agents and shifts are built ONCE and shared by both arms so the Guids stay stable.
/// </remarks>
[TestFixture]
public sealed class WarmStartEfficacyBenchmarkTests
{
    private static readonly int[] Seeds = [11, 23, 37, 53, 71, 97, 113, 131, 151, 173];
    private const int PriorRunSeed = 909;

    private const double FullTimeHours = 180;
    private const double SlotHours = 8;

    private sealed record RunResult(
        int Stage0,
        double Stage1,
        double Stage2,
        double Stage3,
        double Stage4,
        double AggregateFitness,
        int GenerationsRun,
        int LastImprovementGeneration,
        long WallClockMs,
        double CoveragePercent,
        double TargetReachedPercent);

    private sealed class ConvergenceTracker : IProgress<TokenEvolutionProgress>
    {
        private int _prevStage0 = int.MinValue;
        private double _prevStage1 = double.NaN;
        private double _prevStage2 = double.NaN;

        public int LastGeneration { get; private set; }
        public int LastImprovementGeneration { get; private set; }

        public void Report(TokenEvolutionProgress p)
        {
            LastGeneration = p.Generation;
            var changed = p.BestHardViolations != _prevStage0
                          || Math.Abs(p.BestStage1Completion - _prevStage1) > 1e-9
                          || Math.Abs(p.BestStage2Score - _prevStage2) > 1e-9;
            if (changed)
            {
                LastImprovementGeneration = p.Generation;
                _prevStage0 = p.BestHardViolations;
                _prevStage1 = p.BestStage1Completion;
                _prevStage2 = p.BestStage2Score;
            }
        }
    }

    [Test, Explicit("Efficacy benchmark — run manually via FullyQualifiedName filter; prints tables to test output.")]
    public void WarmVsCold_MultiSeed_Aggregate()
    {
        // 1) Build the shared scenario ONCE (stable shift Guids across both arms).
        var agents = BuildAgents();
        var shifts = BuildShifts(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));
        var coldContext = BuildContext(agents, shifts, warmStart: []);

        // 2) Prior "previous month" cold run → derive warm-start assignments from its best plan.
        var priorConfig = FullConfig(PriorRunSeed);
        var priorBest = TokenEvolutionLoop.Create().Run(coldContext, priorConfig);
        var assignments = DeriveAssignments(priorBest);
        var withEmptyRef = assignments.Count(a => a.ShiftRefId == Guid.Empty);

        var warmContext = BuildContext(agents, shifts, warmStart: assignments);

        // 3) Prove the warm arm actually contributes individuals (else we measure cold twice).
        var seededTokens = new WarmStartTokenStrategy()
            .BuildScenario(warmContext, new Random(Seeds[0]))
            .Tokens
            .Count(t => !t.IsLocked);
        var priorNonLocked = priorBest.Tokens.Count(t => !t.IsLocked);
        var survivorRatio = priorNonLocked == 0 ? 0.0 : (double)seededTokens / priorNonLocked;

        TestContext.Out.WriteLine("=== WARM-START DIAGNOSTICS ===");
        TestContext.Out.WriteLine($"prior-run non-locked tokens : {priorNonLocked}");
        TestContext.Out.WriteLine($"derived assignments         : {assignments.Count} (Guid.Empty ref: {withEmptyRef})");
        TestContext.Out.WriteLine($"warm-seeded survivors (seed {Seeds[0]}): {seededTokens}  ratio={survivorRatio:P1}");
        TestContext.Out.WriteLine("");

        Assert.That(withEmptyRef, Is.Zero,
            "Derived assignments carry Guid.Empty ShiftRefId — shift ids are not Guids; the warm arm would drop everything.");
        Assert.That(survivorRatio, Is.GreaterThan(0.5),
            $"Warm arm seeded only {survivorRatio:P1} of the prior plan — 'warm' is barely warm, any null result is an artifact.");

        // 4) Run both arms over all seeds.
        var cold = new List<RunResult>();
        var warm = new List<RunResult>();
        var gen0Cold = new List<double>();
        var gen0Warm = new List<double>();

        foreach (var seed in Seeds)
        {
            gen0Cold.Add(Gen0BestStage1(coldContext, seed));
            gen0Warm.Add(Gen0BestStage1(warmContext, seed));
            cold.Add(RunFull(coldContext, seed));
            warm.Add(RunFull(warmContext, seed));
        }

        // 5) Report.
        DumpGen0Table(gen0Cold, gen0Warm);
        DumpPerSeedTable(cold, warm);
        DumpAggregate(cold, warm, gen0Cold, gen0Warm);
    }

    private static double Gen0BestStage1(CoreWizardContext context, int seed)
    {
        var config = FullConfig(seed) with { MaxGenerations = 0 };
        var best = TokenEvolutionLoop.Create().Run(context, config);
        return best.FitnessStage1;
    }

    private static RunResult RunFull(CoreWizardContext context, int seed)
    {
        var config = FullConfig(seed);
        var tracker = new ConvergenceTracker();
        var sw = Stopwatch.StartNew();
        var best = TokenEvolutionLoop.Create().Run(context, config, tracker);
        sw.Stop();
        var metrics = WizardMetricsCalculator.Compute(best, context, stage1EscalationCount: 0);
        return new RunResult(
            Stage0: best.FitnessStage0,
            Stage1: best.FitnessStage1,
            Stage2: best.FitnessStage2,
            Stage3: best.FitnessStage3,
            Stage4: best.FitnessStage4,
            AggregateFitness: best.Fitness,
            GenerationsRun: tracker.LastGeneration,
            LastImprovementGeneration: tracker.LastImprovementGeneration,
            WallClockMs: sw.ElapsedMilliseconds,
            CoveragePercent: metrics.CoveragePercent,
            TargetReachedPercent: metrics.TargetReachedPercent);
    }

    private static TokenEvolutionConfig FullConfig(int seed) => new()
    {
        PopulationSize = 20,
        MaxGenerations = 50,
        EarlyStopNoImprovementGenerations = 15,
        RandomSeed = seed,
    };

    private static IReadOnlyList<CoreWarmStartAssignment> DeriveAssignments(CoreScenario best) =>
        best.Tokens
            .Where(t => !t.IsLocked)
            .Select(t => new CoreWarmStartAssignment(
                AgentId: t.AgentId,
                Date: t.Date,
                ShiftRefId: t.ShiftRefId,
                StartAt: t.StartAt,
                EndAt: t.EndAt,
                TotalHours: t.TotalHours))
            .ToList();

    private static IReadOnlyList<CoreAgent> BuildAgents()
    {
        var list = new List<CoreAgent>();
        for (var i = 0; i < 8; i++)
        {
            list.Add(FullTimeAgent($"FT-{i:D2}"));
        }
        list.Add(PartTimeAgent("PT-00", 90));
        list.Add(PartTimeAgent("PT-01", 90));
        list.Add(NightSpecialistAgent("NS-00"));
        list.Add(NightSpecialistAgent("NS-01"));
        return list;
    }

    private static IReadOnlyList<CoreShift> BuildShifts(DateOnly from, DateOnly until)
    {
        var shifts = new List<CoreShift>();
        for (var d = from; d <= until; d = d.AddDays(1))
        {
            var iso = d.ToString("yyyy-MM-dd");
            shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "Frueh", iso, "06:00", "14:00", SlotHours, 1, 0));
            shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "Spaet", iso, "14:00", "22:00", SlotHours, 1, 0));
            shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "Nacht", iso, "22:00", "06:00", SlotHours, 1, 0));
        }
        return shifts;
    }

    private static CoreWizardContext BuildContext(
        IReadOnlyList<CoreAgent> agents,
        IReadOnlyList<CoreShift> shifts,
        IReadOnlyList<CoreWarmStartAssignment> warmStart) => new()
    {
        PeriodFrom = new DateOnly(2026, 5, 1),
        PeriodUntil = new DateOnly(2026, 5, 31),
        Agents = agents,
        Shifts = shifts,
        WarmStartAssignments = warmStart,
        SchedulingMaxConsecutiveDays = 6,
        SchedulingMaxDailyHours = 10,
        SchedulingMaxWeeklyHours = 50,
        SchedulingMinPauseHours = 11,
    };

    private static CoreAgent FullTimeAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: FullTimeHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = FullTimeHours,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
        NightRate = 0.10m,
        WE1Rate = 0.25m,
        WE2Rate = 0.50m,
    };

    private static CoreAgent PartTimeAgent(string id, double guaranteed) => FullTimeAgent(id) with
    {
        GuaranteedHours = guaranteed,
        FullTime = guaranteed,
    };

    private static CoreAgent NightSpecialistAgent(string id) => FullTimeAgent(id) with
    {
        GuaranteedHours = 120,
        FullTime = 120,
        NightRate = 0.30m,
    };

    private static void DumpGen0Table(List<double> cold, List<double> warm)
    {
        TestContext.Out.WriteLine("=== START POPULATION (Best-of-Gen-0) Stage1 — higher is better ===");
        TestContext.Out.WriteLine($"{"seed",6} {"cold",10} {"warm",10} {"delta",10}");
        for (var i = 0; i < Seeds.Length; i++)
        {
            TestContext.Out.WriteLine($"{Seeds[i],6} {cold[i],10:F4} {warm[i],10:F4} {warm[i] - cold[i],10:F4}");
        }
        TestContext.Out.WriteLine(
            $"{"mean",6} {Mean(cold),10:F4} {Mean(warm),10:F4} {Mean(warm) - Mean(cold),10:F4}");
        TestContext.Out.WriteLine(
            $"{"median",6} {Median(cold),10:F4} {Median(warm),10:F4} {Median(warm) - Median(cold),10:F4}");
        TestContext.Out.WriteLine("");
    }

    private static void DumpPerSeedTable(List<RunResult> cold, List<RunResult> warm)
    {
        TestContext.Out.WriteLine("=== FINAL PER SEED (C=cold W=warm) ===");
        TestContext.Out.WriteLine(
            $"{"seed",6} {"arm",4} {"stg0",5} {"stg1",8} {"stg2",8} {"fitness",9} {"gensRun",8} {"lastImpr",9} {"ms",7} {"cov%",7} {"tgt%",7}");
        for (var i = 0; i < Seeds.Length; i++)
        {
            WriteRow(Seeds[i], "C", cold[i]);
            WriteRow(Seeds[i], "W", warm[i]);
        }
        TestContext.Out.WriteLine("");
    }

    private static void WriteRow(int seed, string arm, RunResult r)
    {
        TestContext.Out.WriteLine(
            $"{seed,6} {arm,4} {r.Stage0,5} {r.Stage1,8:F4} {r.Stage2,8:F4} {r.AggregateFitness,9:F4} {r.GenerationsRun,8} {r.LastImprovementGeneration,9} {r.WallClockMs,7} {r.CoveragePercent * 100,7:F1} {r.TargetReachedPercent * 100,7:F1}");
    }

    private static void DumpAggregate(
        List<RunResult> cold, List<RunResult> warm, List<double> gen0Cold, List<double> gen0Warm)
    {
        TestContext.Out.WriteLine($"=== AGGREGATE (mean / median over {Seeds.Length} seeds) ===");
        AggLine("gen0 Stage1", gen0Cold, gen0Warm, higherBetter: true);
        AggLine("final Stage0", cold.Select(r => (double)r.Stage0).ToList(), warm.Select(r => (double)r.Stage0).ToList(), higherBetter: false);
        AggLine("final Stage1", cold.Select(r => r.Stage1).ToList(), warm.Select(r => r.Stage1).ToList(), higherBetter: true);
        AggLine("final Stage2", cold.Select(r => r.Stage2).ToList(), warm.Select(r => r.Stage2).ToList(), higherBetter: true);
        AggLine("final Fitness", cold.Select(r => r.AggregateFitness).ToList(), warm.Select(r => r.AggregateFitness).ToList(), higherBetter: true);
        AggLine("generationsRun", cold.Select(r => (double)r.GenerationsRun).ToList(), warm.Select(r => (double)r.GenerationsRun).ToList(), higherBetter: false);
        AggLine("lastImprovementGen", cold.Select(r => (double)r.LastImprovementGeneration).ToList(), warm.Select(r => (double)r.LastImprovementGeneration).ToList(), higherBetter: false);
        AggLine("wallClockMs", cold.Select(r => (double)r.WallClockMs).ToList(), warm.Select(r => (double)r.WallClockMs).ToList(), higherBetter: false);
        AggLine("coverage%", cold.Select(r => r.CoveragePercent * 100).ToList(), warm.Select(r => r.CoveragePercent * 100).ToList(), higherBetter: true);
    }

    private static void AggLine(string label, List<double> cold, List<double> warm, bool higherBetter)
    {
        var meanDelta = Mean(warm) - Mean(cold);
        var verdict = Math.Abs(meanDelta) < 1e-9
            ? "tie"
            : (meanDelta > 0) == higherBetter ? "warm better" : "warm worse";
        TestContext.Out.WriteLine(
            $"{label,-20} cold(mean={Mean(cold),9:F4} med={Median(cold),9:F4})  warm(mean={Mean(warm),9:F4} med={Median(warm),9:F4})  meanDelta={meanDelta,9:F4}  [{verdict}]");
    }

    private static double Mean(List<double> xs) => xs.Count == 0 ? 0.0 : xs.Average();

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0)
        {
            return 0.0;
        }
        var sorted = xs.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }
}
