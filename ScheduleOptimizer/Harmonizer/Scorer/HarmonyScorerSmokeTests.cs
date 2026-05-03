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
    public void Score_UniformEarlyBlocks_OutScoresChaoticBlocks()
    {
        var uniform = BuildSingleRowBitmap(14, day => day % 7 < 4 ? CellSymbol.Early : CellSymbol.Free);
        var chaotic = BuildSingleRowBitmap(14, day => day switch
        {
            0 => CellSymbol.Early,
            2 => CellSymbol.Night,
            5 => CellSymbol.Late,
            6 => CellSymbol.Early,
            9 => CellSymbol.Night,
            12 => CellSymbol.Late,
            _ => CellSymbol.Free,
        });
        var scorer = new HarmonyScorer();

        var uniformScore = scorer.Score(uniform, 0).Score;
        var chaoticScore = scorer.Score(chaotic, 0).Score;

        uniformScore.ShouldBeGreaterThan(chaoticScore);
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
