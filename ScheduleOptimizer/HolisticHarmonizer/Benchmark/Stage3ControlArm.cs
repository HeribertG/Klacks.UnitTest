// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/**
 * Control arm WITHOUT an LLM: per iteration it pulls the UNTRIMMED candidate lists of all three intent
 * generators, evaluates every single candidate and every pair of candidates (on clones) through the very same
 * BatchEvaluator the LLM path uses (hard validator + committee + score-greedy), and applies the best strictly
 * improving batch. Same iteration cap as the engine (10). Stops early when nothing improves.
 * @param pairPoolCap - pairs are formed among the best PairPoolCap singles (ranked by their real end score);
 *                      int.MaxValue means all pairs
 */

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Committee.Agents;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Benchmark;

public sealed record Stage3ControlResult(
    HarmonyBitmap FinalBitmap,
    double FitnessBefore,
    double FitnessAfter,
    int AppliedBatches,
    int Evaluations,
    int MaxCandidatesPerIteration);

public static class Stage3ControlArm
{
    public const int MaxIterations = 10;
    private const double ImprovementEpsilon = 1e-12;
    private const string ControlIntent = "control";

    public static Stage3ControlResult Run(BitmapInput input, HarmonyBitmap stage2Bitmap, int pairPoolCap)
    {
        var working = BitmapCloner.Clone(stage2Bitmap);
        var fitness = new HarmonyFitnessEvaluator(new HarmonyScorer());
        var validator = new PlanMutationValidator(
            new DomainAwareReplaceValidator(input.Availability, input.BoundaryAssignments, input.IneligibleAssignments),
            input.RestrictedTimeWindows);
        var committee = new ConstraintAgentCommittee(new IConstraintAgent[]
        {
            new HoursConstraintAgent(),
            new PauseConstraintAgent(input.BoundaryAssignments),
            new ConsecutiveConstraintAgent(input.BoundaryAssignments),
            new RotationConstraintAgent(),
            new PreferenceConstraintAgent(),
        });
        var evaluator = new BatchEvaluator(validator, fitness, committee);
        var pool = new MoveCandidatePool(
            validator,
            new IMoveCandidateGenerator[]
            {
                new ConsolidateBlockCandidateGenerator(),
                new EnlargePauseCandidateGenerator(),
                new RedistributeLoadCandidateGenerator(),
            },
            topPerIntent: int.MaxValue);

        var fitnessBefore = fitness.Evaluate(working).Fitness;
        var applied = 0;
        var evaluations = 0;
        var maxCandidates = 0;

        for (var iter = 0; iter < MaxIterations; iter++)
        {
            var candidates = CollectCandidates(pool, working);
            maxCandidates = Math.Max(maxCandidates, candidates.Count);
            var scoreBefore = fitness.Evaluate(working).Fitness;

            var singles = new List<(PlanCellSwap Swap, double Score)>(candidates.Count);
            MutationBatch? best = null;
            var bestScore = scoreBefore;
            foreach (var swap in candidates)
            {
                var evaluation = evaluator.Evaluate(BitmapCloner.Clone(working), Batch(swap));
                evaluations++;
                singles.Add((swap, evaluation.ScoreAfter));
                if (IsAccepted(evaluation) && evaluation.ScoreAfter > bestScore + ImprovementEpsilon)
                {
                    bestScore = evaluation.ScoreAfter;
                    best = Batch(swap);
                }
            }

            var pairBase = singles
                .OrderByDescending(s => s.Score)
                .Take(pairPoolCap)
                .Select(s => s.Swap)
                .ToList();
            for (var i = 0; i < pairBase.Count; i++)
            {
                for (var j = i + 1; j < pairBase.Count; j++)
                {
                    var batch = Batch(pairBase[i], pairBase[j]);
                    var evaluation = evaluator.Evaluate(BitmapCloner.Clone(working), batch);
                    evaluations++;
                    if (IsAccepted(evaluation) && evaluation.ScoreAfter > bestScore + ImprovementEpsilon)
                    {
                        bestScore = evaluation.ScoreAfter;
                        best = batch;
                    }
                }
            }

            if (best is null)
            {
                break;
            }

            evaluator.Evaluate(working, best);
            applied++;
        }

        return new Stage3ControlResult(working, fitnessBefore, fitness.Evaluate(working).Fitness, applied, evaluations, maxCandidates);
    }

    private static List<PlanCellSwap> CollectCandidates(MoveCandidatePool pool, HarmonyBitmap working)
    {
        var seen = new HashSet<(int, int, int, int)>();
        var result = new List<PlanCellSwap>();
        foreach (var intent in HolisticIntent.All)
        {
            foreach (var candidate in pool.Generate(working, intent))
            {
                var key = (Math.Min(candidate.RowA, candidate.RowB), Math.Max(candidate.RowA, candidate.RowB),
                    Math.Min(candidate.DayA, candidate.DayB), Math.Max(candidate.DayA, candidate.DayB));
                if (seen.Add(key))
                {
                    result.Add(new PlanCellSwap(candidate.RowA, candidate.DayA, candidate.RowB, candidate.DayB, candidate.Hint));
                }
            }
        }
        return result;
    }

    private static bool IsAccepted(BatchEvaluation evaluation)
        => evaluation.Result is BatchAcceptance.Accepted or BatchAcceptance.PartiallyAccepted;

    private static MutationBatch Batch(params PlanCellSwap[] steps)
        => new(Guid.NewGuid(), ControlIntent, LlmIteration: 0, Steps: steps);
}
