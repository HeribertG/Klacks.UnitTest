// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.Scoring;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Scoring;

[TestFixture]
public class EngineScoreSerializerTests
{
    private static DetailedFitnessResult MakeResult()
    {
        return new DetailedFitnessResult(
            Stage0: 0,
            Stage1: 0.97,
            Stage2: 0.91,
            Stage3: 0.78,
            Stage4: 0.66,
            Stage3Components: new Stage3Components(BlockOrder: 0.82, Blacklist: 1.0, Location: 0.61, MaxGap: 0.74),
            Stage4Components: new Stage4Components(Fairness: 0.71, MinimumHours: 0.88, BlockSymmetry: 0.42));
    }

    private static CoreWizardContext MakeContext(bool warmStart)
    {
        var from = new DateOnly(2026, 4, 1);
        var until = new DateOnly(2026, 4, 30);
        var warmAssignments = warmStart
            ? new List<CoreWarmStartAssignment>
            {
                new("A", from, Guid.NewGuid(), from.ToDateTime(new TimeOnly(8, 0)), from.ToDateTime(new TimeOnly(16, 0)), 8),
            }
            : [];

        return new CoreWizardContext
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents =
            [
                new CoreAgent("A", 0, 0, 6, 11, 0.5, 10, 50, 2),
                new CoreAgent("B", 0, 0, 6, 11, 0.5, 10, 50, 2),
            ],
            Shifts =
            [
                new CoreShift(Guid.NewGuid().ToString(), "FD", "2026-04-01", "08:00", "16:00", 8, 1, 0),
            ],
            LockedWorks =
            [
                new CoreLockedWork("w1", "A", from, 0, 8, from.ToDateTime(new TimeOnly(8, 0)), from.ToDateTime(new TimeOnly(16, 0)), Guid.NewGuid(), null),
            ],
            WarmStartAssignments = warmAssignments,
        };
    }

    [Test]
    public void Serialize_ContainsAllMandatoryTopLevelFields()
    {
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), new TokenEvolutionConfig(), MakeContext(warmStart: true));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("v").GetInt32().ShouldBe(EngineScoreSerializer.SchemaVersion);
        root.GetProperty("engine").GetString().ShouldBe(EngineScoreSerializer.TokenEvolutionEngineTag);
        root.TryGetProperty("config", out _).ShouldBeTrue();
        root.TryGetProperty("stages", out _).ShouldBeTrue();
        root.TryGetProperty("stage3", out _).ShouldBeTrue();
        root.TryGetProperty("stage4", out _).ShouldBeTrue();
        root.TryGetProperty("context", out _).ShouldBeTrue();
    }

    [Test]
    public void Serialize_EmitsStageAggregatesFromResult()
    {
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), new TokenEvolutionConfig(), MakeContext(warmStart: false));

        using var doc = JsonDocument.Parse(json);
        var stages = doc.RootElement.GetProperty("stages");

        stages.GetProperty("s0").GetInt32().ShouldBe(0);
        stages.GetProperty("s1").GetDouble().ShouldBe(0.97);
        stages.GetProperty("s2").GetDouble().ShouldBe(0.91);
        stages.GetProperty("s3").GetDouble().ShouldBe(0.78);
        stages.GetProperty("s4").GetDouble().ShouldBe(0.66);
    }

    [Test]
    public void Serialize_EmitsStageComponentBreakdown()
    {
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), new TokenEvolutionConfig(), MakeContext(warmStart: false));

        using var doc = JsonDocument.Parse(json);
        var stage3 = doc.RootElement.GetProperty("stage3");
        stage3.GetProperty("blockOrder").GetDouble().ShouldBe(0.82);
        stage3.GetProperty("blacklist").GetDouble().ShouldBe(1.0);
        stage3.GetProperty("location").GetDouble().ShouldBe(0.61);
        stage3.GetProperty("maxGap").GetDouble().ShouldBe(0.74);

        var stage4 = doc.RootElement.GetProperty("stage4");
        stage4.GetProperty("fairness").GetDouble().ShouldBe(0.71);
        stage4.GetProperty("minimumHours").GetDouble().ShouldBe(0.88);
        stage4.GetProperty("blockSymmetry").GetDouble().ShouldBe(0.42);
    }

    [Test]
    public void Serialize_EmitsConfigCoreValuesIncludingWarmStartRatioAndSeed()
    {
        var config = new TokenEvolutionConfig { RandomSeed = 42 };
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), config, MakeContext(warmStart: false));

        using var doc = JsonDocument.Parse(json);
        var cfg = doc.RootElement.GetProperty("config");

        cfg.GetProperty("stage1RankDecay").GetDouble().ShouldBe(config.FitnessStage1RankDecay);
        cfg.GetProperty("stage2Decay").GetDouble().ShouldBe(config.FitnessStage2Decay);
        cfg.GetProperty("initWarmStartRatio").GetDouble().ShouldBe(config.InitWarmStartRatio);
        cfg.GetProperty("seed").GetInt32().ShouldBe(42);

        var weights = cfg.GetProperty("stage3Weights");
        weights.GetProperty("blockOrder").GetDouble().ShouldBe(config.FitnessStage3BlockOrder);
        weights.GetProperty("blacklist").GetDouble().ShouldBe(config.FitnessStage3Blacklist);
        weights.GetProperty("location").GetDouble().ShouldBe(config.FitnessStage3Location);
        weights.GetProperty("maxGap").GetDouble().ShouldBe(config.FitnessStage3MaxGap);
    }

    [Test]
    public void Serialize_EmitsContextFeaturesWithWarmStartTrue()
    {
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), new TokenEvolutionConfig(), MakeContext(warmStart: true));

        using var doc = JsonDocument.Parse(json);
        var context = doc.RootElement.GetProperty("context");

        context.GetProperty("agents").GetInt32().ShouldBe(2);
        context.GetProperty("shifts").GetInt32().ShouldBe(1);
        context.GetProperty("days").GetInt32().ShouldBe(30);
        context.GetProperty("warmStart").GetBoolean().ShouldBeTrue();
        context.GetProperty("lockedRatio").GetDouble().ShouldBe(1.0 / (2 * 30), 1e-9);
    }

    [Test]
    public void Serialize_WarmStartFalse_WhenNoWarmStartAssignments()
    {
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), new TokenEvolutionConfig(), MakeContext(warmStart: false));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("context").GetProperty("warmStart").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public void Serialize_ProducesStablyReparsableJson()
    {
        var json = EngineScoreSerializer.SerializeTokenEvolution(MakeResult(), new TokenEvolutionConfig(), MakeContext(warmStart: true));

        Should.NotThrow(() =>
        {
            using var doc = JsonDocument.Parse(json);
        });
    }

    private static Cell Work(CellSymbol symbol, DateOnly date)
    {
        return new Cell(symbol, Guid.NewGuid(), [Guid.NewGuid()], false,
            date.ToDateTime(new TimeOnly(8, 0)), date.ToDateTime(new TimeOnly(16, 0)), 8m);
    }

    private static HarmonyBitmap MakeHarmonyBitmap()
    {
        var days = new List<DateOnly>
        {
            new(2026, 4, 1), new(2026, 4, 2), new(2026, 4, 3), new(2026, 4, 4), new(2026, 4, 5),
        };
        var rows = new List<BitmapAgent>
        {
            new("A", "A", 40m, new HashSet<CellSymbol>()),
            new("B", "B", 40m, new HashSet<CellSymbol>()),
        };
        var cells = new Cell[2, 5];
        for (var r = 0; r < 2; r++)
        {
            for (var d = 0; d < 5; d++)
            {
                cells[r, d] = d < 3 ? Work(CellSymbol.Early, days[d]) : Cell.Free();
            }
        }
        return new HarmonyBitmap(rows, days, cells);
    }

    [Test]
    public void SerializeHarmonizer_ContainsAllMandatoryTopLevelFields()
    {
        var json = EngineScoreSerializer.SerializeHarmonizer(MakeHarmonyBitmap(), new HarmonizerEvolutionConfig(), globalFitness: 0.73);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("v").GetInt32().ShouldBe(EngineScoreSerializer.SchemaVersion);
        root.GetProperty("engine").GetString().ShouldBe(EngineScoreSerializer.HarmonizerEngineTag);
        root.GetProperty("fitness").GetDouble().ShouldBe(0.73);
        root.TryGetProperty("components", out _).ShouldBeTrue();
        root.TryGetProperty("config", out _).ShouldBeTrue();
        root.TryGetProperty("context", out _).ShouldBeTrue();
    }

    [Test]
    public void SerializeHarmonizer_EmitsRealHarmonyScorerComponentNames()
    {
        var json = EngineScoreSerializer.SerializeHarmonizer(MakeHarmonyBitmap(), new HarmonizerEvolutionConfig(), globalFitness: 0.5);

        using var doc = JsonDocument.Parse(json);
        var components = doc.RootElement.GetProperty("components");

        foreach (var name in new[]
        {
            "blockSizeUniformity", "restUniformity", "blockHomogeneity", "transitionCompliance",
            "shiftTypeRotation", "preferredShiftFraction", "targetHoursDeviation",
        })
        {
            components.TryGetProperty(name, out var value).ShouldBeTrue($"missing component {name}");
            value.GetDouble().ShouldBeInRange(0.0, 1.0);
        }
    }

    [Test]
    public void SerializeHarmonizer_EmitsConfigSnapshotFromEffectiveConfig()
    {
        var config = new HarmonizerEvolutionConfig(PopulationSize: 12, MaxGenerations: 30, EliteCount: 3, Seed: 7);
        var json = EngineScoreSerializer.SerializeHarmonizer(MakeHarmonyBitmap(), config, globalFitness: 0.5);

        using var doc = JsonDocument.Parse(json);
        var cfg = doc.RootElement.GetProperty("config");

        cfg.GetProperty("populationSize").GetInt32().ShouldBe(12);
        cfg.GetProperty("maxGenerations").GetInt32().ShouldBe(30);
        cfg.GetProperty("eliteCount").GetInt32().ShouldBe(3);
        cfg.GetProperty("seed").GetInt32().ShouldBe(7);
    }

    [Test]
    public void SerializeHarmonizer_ContextHasAgentsDaysAndLockedRatio()
    {
        var json = EngineScoreSerializer.SerializeHarmonizer(MakeHarmonyBitmap(), new HarmonizerEvolutionConfig(), globalFitness: 0.5);

        using var doc = JsonDocument.Parse(json);
        var context = doc.RootElement.GetProperty("context");

        context.GetProperty("agents").GetInt32().ShouldBe(2);
        context.GetProperty("days").GetInt32().ShouldBe(5);
        context.GetProperty("lockedRatio").GetDouble().ShouldBe(0.0);
    }

    [Test]
    public void SerializeHolistic_ContainsMandatoryFieldsAndOmitsConfig()
    {
        var json = EngineScoreSerializer.SerializeHolistic(MakeHarmonyBitmap(), globalFitness: 0.66);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("v").GetInt32().ShouldBe(EngineScoreSerializer.SchemaVersion);
        root.GetProperty("engine").GetString().ShouldBe(EngineScoreSerializer.HolisticEngineTag);
        root.GetProperty("fitness").GetDouble().ShouldBe(0.66);
        root.TryGetProperty("components", out _).ShouldBeTrue();
        root.TryGetProperty("context", out _).ShouldBeTrue();
        root.TryGetProperty("config", out _).ShouldBeFalse();
    }

    [Test]
    public void SerializeHarmonizerAndHolistic_ProduceStablyReparsableJson()
    {
        Should.NotThrow(() =>
        {
            using var h = JsonDocument.Parse(EngineScoreSerializer.SerializeHarmonizer(MakeHarmonyBitmap(), new HarmonizerEvolutionConfig(), 0.4));
            using var l = JsonDocument.Parse(EngineScoreSerializer.SerializeHolistic(MakeHarmonyBitmap(), 0.4));
        });
    }
}
