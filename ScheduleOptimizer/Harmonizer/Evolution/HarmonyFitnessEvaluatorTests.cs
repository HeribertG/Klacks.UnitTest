// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Evolution;

[TestFixture]
public class HarmonyFitnessEvaluatorTests
{
    [Test]
    public void Evaluate_EmptyBitmap_ReturnsOne()
    {
        var bitmap = BuildEmptyBitmap(0, 1);
        var evaluator = new HarmonyFitnessEvaluator(new HarmonyScorer());

        var result = evaluator.Evaluate(bitmap);

        result.Fitness.ShouldBe(1.0);
        result.RowScores.Count.ShouldBe(0);
    }

    [Test]
    public void Evaluate_SingleRow_FitnessEqualsRowScore()
    {
        var bitmap = BuildEmptyBitmap(1, 7);
        var evaluator = new HarmonyFitnessEvaluator(new HarmonyScorer());

        var result = evaluator.Evaluate(bitmap);

        result.RowScores.Count.ShouldBe(1);
        result.Fitness.ShouldBe(result.RowScores[0]);
    }

    [Test]
    public void Evaluate_UpperRowDominatesGlobalFitness()
    {
        var goodOnTop = BuildPatternBitmap(topUniform: true);
        var goodOnBottom = BuildPatternBitmap(topUniform: false);
        var evaluator = new HarmonyFitnessEvaluator(new HarmonyScorer());

        var topGood = evaluator.Evaluate(goodOnTop).Fitness;
        var bottomGood = evaluator.Evaluate(goodOnBottom).Fitness;

        topGood.ShouldBeGreaterThan(bottomGood);
    }

    private static HarmonyBitmap BuildEmptyBitmap(int rows, int days)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>()));
        }
        var startDate = new DateOnly(2026, 1, 1);
        var input = new BitmapInput(agents, startDate, startDate.AddDays(Math.Max(days - 1, 0)), []);
        return BitmapBuilder.Build(input);
    }

    private static HarmonyBitmap BuildPatternBitmap(bool topUniform)
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Top", 200m, new HashSet<CellSymbol>()),
            new("agent-1", "Bottom", 100m, new HashSet<CellSymbol>()),
        };
        var startDate = new DateOnly(2026, 1, 1);
        var assignments = new List<BitmapAssignment>();

        var topSymbols = topUniform
            ? new[] { CellSymbol.Early, CellSymbol.Early, CellSymbol.Free, CellSymbol.Early, CellSymbol.Early, CellSymbol.Free, CellSymbol.Free }
            : new[] { CellSymbol.Early, CellSymbol.Night, CellSymbol.Late, CellSymbol.Free, CellSymbol.Night, CellSymbol.Early, CellSymbol.Free };
        var bottomSymbols = topUniform
            ? new[] { CellSymbol.Early, CellSymbol.Night, CellSymbol.Late, CellSymbol.Free, CellSymbol.Night, CellSymbol.Early, CellSymbol.Free }
            : new[] { CellSymbol.Early, CellSymbol.Early, CellSymbol.Free, CellSymbol.Early, CellSymbol.Early, CellSymbol.Free, CellSymbol.Free };

        for (var d = 0; d < topSymbols.Length; d++)
        {
            if (topSymbols[d] != CellSymbol.Free)
            {
                assignments.Add(new BitmapAssignment("agent-0", startDate.AddDays(d), topSymbols[d], Guid.NewGuid(), [Guid.NewGuid()], false));
            }
            if (bottomSymbols[d] != CellSymbol.Free)
            {
                assignments.Add(new BitmapAssignment("agent-1", startDate.AddDays(d), bottomSymbols[d], Guid.NewGuid(), [Guid.NewGuid()], false));
            }
        }
        var input = new BitmapInput(agents, startDate, startDate.AddDays(topSymbols.Length - 1), assignments);
        return BitmapBuilder.Build(input);
    }
}
