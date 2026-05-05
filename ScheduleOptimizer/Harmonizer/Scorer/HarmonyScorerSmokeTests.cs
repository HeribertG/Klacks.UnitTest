// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Scorer;

[TestFixture]
public class HarmonyScorerSmokeTests
{
    [Test]
    public void LoadDefaultRuleBase_ResolvesEmbeddedResource()
    {
        var rules = HarmonyRuleBaseLoader.LoadDefault();

        rules.ShouldNotBeEmpty();
        foreach (var rule in rules)
        {
            rule.ConsequentVariable.ShouldBe(HarmonyLinguisticVariables.HarmonyScore);
        }
    }

    [Test]
    public void Score_AllFreeRow_ReturnsHighScore()
    {
        var bitmap = BuildSingleRowBitmap(7, _ => CellSymbol.Free);
        var scorer = new HarmonyScorer();

        var result = scorer.Score(bitmap, 0);

        result.Score.ShouldBeGreaterThan(0.7);
        result.Features.WorkBlockCount.ShouldBe(0);
    }

    [Test]
    public void Score_RotatedBlocks_OutScoreSingleTypeBlocks()
    {
        // Two equally-uniform plans (same block sizes, same rest periods) — the only
        // difference is shift-type rotation: rotated cycles E→L→N across blocks while
        // singleType keeps all blocks Early. The new ShiftTypeRotation feature must
        // reward rotated higher.
        var rotated = BuildSingleRowBitmap(28, day => (day / 7) switch
        {
            0 when day % 7 < 4 => CellSymbol.Early,
            1 when day % 7 < 4 => CellSymbol.Late,
            2 when day % 7 < 4 => CellSymbol.Night,
            3 when day % 7 < 4 => CellSymbol.Early,
            _ => CellSymbol.Free,
        });
        var singleType = BuildSingleRowBitmap(28, day => day % 7 < 4 ? CellSymbol.Early : CellSymbol.Free);
        var scorer = new HarmonyScorer();

        var rotatedScore = scorer.Score(rotated, 0).Score;
        var singleTypeScore = scorer.Score(singleType, 0).Score;

        rotatedScore.ShouldBeGreaterThan(singleTypeScore);
    }

    [Test]
    public void Score_RestPeriodVariance_LowersScore()
    {
        // Two plans with the same shift symbols and equal block sizes; only the rest
        // periods between blocks differ. The plan with uniform rest gaps must score
        // higher than the one with widely varying rests.
        var uniformRest = BuildSingleRowBitmap(20, day => (day % 5 < 3) ? CellSymbol.Early : CellSymbol.Free);
        var unevenRest = BuildSingleRowBitmap(20, day => day switch
        {
            0 or 1 or 2 => CellSymbol.Early,
            5 or 6 or 7 => CellSymbol.Early,
            14 or 15 or 16 => CellSymbol.Early,
            _ => CellSymbol.Free,
        });
        var scorer = new HarmonyScorer();

        var uniformRestScore = scorer.Score(uniformRest, 0).Features.RestUniformity;
        var unevenRestScore = scorer.Score(unevenRest, 0).Features.RestUniformity;

        uniformRestScore.ShouldBeGreaterThan(unevenRestScore);
    }

    [Test]
    public void Score_ReverseTransition_HasLowerComplianceThanForward()
    {
        var forward = BuildSingleRowBitmap(9, day => day switch
        {
            0 or 1 => CellSymbol.Early,
            3 or 4 => CellSymbol.Late,
            6 or 7 => CellSymbol.Night,
            _ => CellSymbol.Free,
        });
        var backward = BuildSingleRowBitmap(9, day => day switch
        {
            0 or 1 => CellSymbol.Night,
            3 or 4 => CellSymbol.Late,
            6 or 7 => CellSymbol.Early,
            _ => CellSymbol.Free,
        });
        var scorer = new HarmonyScorer();

        var forwardCompliance = scorer.Score(forward, 0).Features.TransitionCompliance;
        var backwardCompliance = scorer.Score(backward, 0).Features.TransitionCompliance;

        forwardCompliance.ShouldBe(1.0);
        backwardCompliance.ShouldBe(0.0);
    }

    private static HarmonyBitmap BuildSingleRowBitmap(int days, Func<int, CellSymbol> dayToSymbol)
    {
        var agent = new BitmapAgent("agent-1", "Test Agent", 100m, new HashSet<CellSymbol>());
        var startDate = new DateOnly(2026, 1, 1);
        var assignments = new List<BitmapAssignment>();
        for (var d = 0; d < days; d++)
        {
            var symbol = dayToSymbol(d);
            if (symbol == CellSymbol.Free)
            {
                continue;
            }
            assignments.Add(new BitmapAssignment(
                agent.Id,
                startDate.AddDays(d),
                symbol,
                Guid.NewGuid(),
                [Guid.NewGuid()],
                false));
        }
        var input = new BitmapInput([agent], startDate, startDate.AddDays(days - 1), assignments);
        return BitmapBuilder.Build(input);
    }
}
