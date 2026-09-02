// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnEvalScorerTests
{
    private const string ToolName = "add_client_note";
    private const string OtherToolName = "navigate_to";
    private const double Precision = 0.000001;

    [Test]
    public void ScoreItem_ExactSlot_NormalizesUmlautCaseAndWhitespace()
    {
        var item = ToolItem(ToolName, ExactSlot("lastName", "Müller"));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["lastName"] = "  MULLER  " });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ToolHit.ShouldBe(true);
        result.SlotScore.ShouldBe(1.0);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_ContainsSlot_MatchesSubstring()
    {
        var item = ToolItem(ToolName, ContainsSlot("phone", "552"));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["phone"] = "031 552 71 90" });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(1.0);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_ContainsSlot_MissingSubstring_ScoresZero()
    {
        var item = ToolItem(ToolName, ContainsSlot("phone", "999"));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["phone"] = "031 552 71 90" });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(0.0);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_OnlyIgnoreSlots_SlotScoreIsNotMeasured()
    {
        var item = ToolItem(ToolName, IgnoreSlot("firstName"));
        var replay = SuccessReplay(ToolName);

        var result = TurnEvalScorer.ScoreItem(item, replay);

        // Nothing was compared, so there is no slot score. Scorer version 1 returned 1.0 here.
        result.SlotScore.ShouldBeNull();
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_NoExpectedSlots_SlotScoreIsNotMeasured()
    {
        var item = ToolItem(ToolName);
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["lastName"] = "Müller" });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ToolHit.ShouldBe(true);
        result.SlotScore.ShouldBeNull();
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void Aggregate_ItemsWithoutExpectedSlots_LeaveSlotDimensionUnmeasured()
    {
        var item = ToolItem(ToolName);
        var replay = SuccessReplay(ToolName);

        var dimensions = TurnEvalScorer.Aggregate([TurnEvalScorer.ScoreItem(item, replay)]);

        // The whole point of scorer version 2: a goldset without expected slots must not report a
        // perfect slot accuracy, and the composite must not be dominated by that phantom value.
        dimensions.SlotAccuracy.ShouldBeNull();
        TurnEvalScorer.ComputeComposite(dimensions).ShouldBe(1.0, Precision);
    }

    [Test]
    public void ScoreItem_MissingParameter_ScoresSlotZero()
    {
        var item = ToolItem(ToolName, ExactSlot("note", "weekend"));
        var replay = SuccessReplay(ToolName);

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(0.0);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_MixedSlots_AveragesSlotScore()
    {
        var item = ToolItem(
            ToolName,
            ExactSlot("lastName", "Müller"),
            ExactSlot("note", "weekend"),
            IgnoreSlot("firstName"));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object>
        {
            ["lastName"] = "muller",
            ["note"] = "prefers night shifts"
        });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldNotBeNull();
        result.SlotScore!.Value.ShouldBe(0.5, Precision);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_ResolvedEntitySlot_VerdictTrue_CountsAsResolved()
    {
        var item = ToolItem(ToolName, ResolvedSlot("lastName", 990001));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["lastName"] = "Muller" });
        var verdicts = new Dictionary<string, bool> { ["lastName"] = true };

        var result = TurnEvalScorer.ScoreItem(item, replay, verdicts);

        result.NameSlotsEvaluated.ShouldBe(1);
        result.NameSlotsResolved.ShouldBe(1);
        result.SlotScore.ShouldBe(1.0);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_ResolvedEntitySlot_VerdictFalse_CountsAsEvaluatedOnly()
    {
        var item = ToolItem(ToolName, ResolvedSlot("lastName", 990001));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["lastName"] = "Meier" });
        var verdicts = new Dictionary<string, bool> { ["lastName"] = false };

        var result = TurnEvalScorer.ScoreItem(item, replay, verdicts);

        result.NameSlotsEvaluated.ShouldBe(1);
        result.NameSlotsResolved.ShouldBe(0);
        result.SlotScore.ShouldBe(0.0);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_ResolvedEntitySlot_WithoutVerdicts_NotResolved()
    {
        var item = ToolItem(ToolName, ResolvedSlot("lastName", 990001));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["lastName"] = "Muller" });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.NameSlotsEvaluated.ShouldBe(1);
        result.NameSlotsResolved.ShouldBe(0);
        result.SlotScore.ShouldBe(0.0);
    }

    [Test]
    public void ScoreItem_ToolHit_IsCaseInsensitive()
    {
        var item = ToolItem(ToolName);
        var replay = SuccessReplay("Add_Client_Note");

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ToolHit.ShouldBe(true);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_AlternativeTool_CountsAsHit()
    {
        var item = ToolItem(ToolName);
        item.AlternativeTools.Add(OtherToolName);
        var replay = SuccessReplay(OtherToolName);

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ToolHit.ShouldBe(true);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_WrongTool_NoSlotScore()
    {
        var item = ToolItem(ToolName, ExactSlot("note", "weekend"));
        var replay = SuccessReplay(OtherToolName, new Dictionary<string, object> { ["note"] = "weekend" });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ToolHit.ShouldBe(false);
        result.SlotScore.ShouldBeNull();
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_NoToolItem_NoToolChosen_Passes()
    {
        var item = NoToolItem();
        var replay = SuccessReplay(null);

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.NoToolCorrect.ShouldBe(true);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_NoToolItem_ToolChosen_Fails()
    {
        var item = NoToolItem();
        var replay = SuccessReplay(ToolName);

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.NoToolCorrect.ShouldBe(false);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_ErroredReplay_ToolItem_CountsAsMiss()
    {
        var item = ToolItem(ToolName);
        var replay = new TurnReplayResult { Success = false, Error = "provider timeout" };

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.Errored.ShouldBeTrue();
        result.ToolHit.ShouldBe(false);
        result.Passed.ShouldBeFalse();
        result.Error.ShouldBe("provider timeout");
    }

    [Test]
    public void ScoreItem_ErroredReplay_NoToolItem_Fails()
    {
        var item = NoToolItem();
        var replay = new TurnReplayResult { Success = false, Error = "provider timeout" };

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.Errored.ShouldBeTrue();
        result.NoToolCorrect.ShouldBe(false);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_RecipeWouldForce_ExcludesItem()
    {
        var item = ToolItem(ToolName);
        var replay = SuccessReplay(ToolName);
        replay.RecipeWouldForce = true;

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.Excluded.ShouldBeTrue();
        result.RecipeWouldForce.ShouldBeTrue();
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ScoreItem_JsonElementStringParameter_MatchesNormalized()
    {
        var item = ToolItem(ToolName, ExactSlot("lastName", "muller"));
        var replay = SuccessReplay(ToolName, JsonParameters("{\"lastName\":\"MÜLLER\"}"));

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(1.0);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_JsonElementNumberParameter_MatchesRawText()
    {
        var item = ToolItem(ToolName, ExactSlot("count", "42"));
        var replay = SuccessReplay(ToolName, JsonParameters("{\"count\":42}"));

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(1.0);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_ParameterKeyLookup_IsCaseInsensitive()
    {
        var item = ToolItem(ToolName, ExactSlot("lastName", "Müller"));
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["LASTNAME"] = "muller" });

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(1.0);
    }

    // W0.5: recipe items measure "did the expected recipe engage" instead of being excluded.
    [Test]
    public void ScoreItem_ExpectedRecipe_MatchingForcedRecipe_PassesAndIsNotExcluded()
    {
        var item = new TurnGoldsetItem { Id = "recipe-1", Message = "onboard employee", ExpectedRecipe = "onboard-employee" };
        var replay = new TurnReplayResult
        {
            Success = true,
            RecipeWouldForce = true,
            ForcedRecipeName = "onboard-employee"
        };

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.RecipeHit.ShouldBe(true);
        result.Excluded.ShouldBe(false);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_ExpectedRecipe_MatchingEngineRecipe_Passes()
    {
        var item = new TurnGoldsetItem { Id = "recipe-2", Message = "plan", ExpectedRecipe = "setup-planning-profile" };
        var replay = new TurnReplayResult
        {
            Success = true,
            EngineRecipeWouldTrigger = true,
            TriggeredRecipeName = "setup-planning-profile"
        };

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.RecipeHit.ShouldBe(true);
        result.Excluded.ShouldBe(false);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void ScoreItem_ExpectedRecipe_WrongRecipe_FailsButIsNotExcluded()
    {
        var item = new TurnGoldsetItem { Id = "recipe-3", Message = "x", ExpectedRecipe = "onboard-employee" };
        var replay = new TurnReplayResult
        {
            Success = true,
            RecipeWouldForce = true,
            ForcedRecipeName = "create-shift-order"
        };

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.RecipeHit.ShouldBe(false);
        result.Excluded.ShouldBe(false);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void Aggregate_RecipeItems_DoNotPolluteNoToolAccuracy()
    {
        var dimensions = TurnEvalScorer.Aggregate(new[]
        {
            new TurnEvalItemResult { ExpectedRecipe = "onboard-employee", RecipeHit = true, Passed = true }
        });

        dimensions.NoToolAccuracy.ShouldBeNull();
        dimensions.ToolAccuracy.ShouldBeNull();
        dimensions.ItemsTotal.ShouldBe(1);
        dimensions.ItemsPassed.ShouldBe(1);
    }

    [Test]
    public void Aggregate_MixedItems_ComputesAllDimensions()
    {
        var items = MixedItems();

        var dimensions = TurnEvalScorer.Aggregate(items);

        dimensions.ToolAccuracy.ShouldNotBeNull();
        dimensions.ToolAccuracy!.Value.ShouldBe(1.0 / 3.0, Precision);
        dimensions.SlotAccuracy.ShouldNotBeNull();
        dimensions.SlotAccuracy!.Value.ShouldBe(1.0, Precision);
        dimensions.NoToolAccuracy.ShouldNotBeNull();
        dimensions.NoToolAccuracy!.Value.ShouldBe(1.0, Precision);
        dimensions.NameResolutionAccuracy.ShouldNotBeNull();
        dimensions.NameResolutionAccuracy!.Value.ShouldBe(0.5, Precision);
        dimensions.AvgLatencyMs.ShouldBe(2000.0, Precision);
        dimensions.TotalCost.ShouldBe(0.05m);
        dimensions.ItemsTotal.ShouldBe(5);
        dimensions.ItemsPassed.ShouldBe(2);
        dimensions.ItemsExcluded.ShouldBe(1);
        dimensions.ItemsErrored.ShouldBe(1);
    }

    [Test]
    public void Aggregate_ExcludedItems_RemovedFromAccuracyDimensions()
    {
        var excludedHit = new TurnEvalItemResult
        {
            ExpectedTool = ToolName,
            ToolHit = true,
            SlotScore = 1.0,
            NameSlotsEvaluated = 1,
            NameSlotsResolved = 1,
            Excluded = true,
            RecipeWouldForce = true
        };
        var activeMiss = new TurnEvalItemResult
        {
            ExpectedTool = ToolName,
            ToolHit = false
        };

        var dimensions = TurnEvalScorer.Aggregate(new[] { excludedHit, activeMiss });

        dimensions.ToolAccuracy.ShouldBe(0.0);
        dimensions.SlotAccuracy.ShouldBeNull();
        dimensions.NameResolutionAccuracy.ShouldBeNull();
        dimensions.ItemsExcluded.ShouldBe(1);
        dimensions.ItemsPassed.ShouldBe(0);
    }

    [Test]
    public void Aggregate_EmptyCategories_YieldNullDimensions()
    {
        var items = new[]
        {
            new TurnEvalItemResult { ExpectedTool = null, NoToolCorrect = true, Passed = true }
        };

        var dimensions = TurnEvalScorer.Aggregate(items);

        dimensions.ToolAccuracy.ShouldBeNull();
        dimensions.SlotAccuracy.ShouldBeNull();
        dimensions.NameResolutionAccuracy.ShouldBeNull();
        dimensions.NoToolAccuracy.ShouldBe(1.0);
    }

    [Test]
    public void ComputeComposite_AllDimensions_WeightedSum()
    {
        var dimensions = new TurnEvalDimensions(
            HonestyAccuracy: 1.0,
            ToolAccuracy: 1.0,
            SlotAccuracy: 0.8,
            NoToolAccuracy: 1.0,
            NameResolutionAccuracy: 1.0,
            AvgLatencyMs: 2000,
            TotalCost: 0m,
            ItemsTotal: 4,
            ItemsPassed: 4,
            ItemsExcluded: 0,
            ItemsErrored: 0);

        var composite = TurnEvalScorer.ComputeComposite(dimensions);

        // tool 0.45*1.0 + slot 0.20*0.8 + noTool 0.10*1.0 + honesty 0.15*1.0 = 0.86 over weight 0.90.
        composite.ShouldBe(0.86 / 0.90, Precision);
    }

    [Test]
    public void ComputeComposite_MissingDimensions_Renormalizes()
    {
        var dimensions = new TurnEvalDimensions(
            ToolAccuracy: 0.5,
            SlotAccuracy: null,
            NoToolAccuracy: null,
            NameResolutionAccuracy: null,
            AvgLatencyMs: 4000,
            TotalCost: 0m,
            ItemsTotal: 1,
            ItemsPassed: 0,
            ItemsExcluded: 0,
            ItemsErrored: 0);

        var composite = TurnEvalScorer.ComputeComposite(dimensions);

        composite.ShouldBe(0.5, Precision);
    }

    [Test]
    public void ComputeComposite_IsIndependentOfLatency()
    {
        var fast = new TurnEvalDimensions(
            ToolAccuracy: 1.0,
            SlotAccuracy: 1.0,
            NoToolAccuracy: 1.0,
            NameResolutionAccuracy: 1.0,
            AvgLatencyMs: 10,
            TotalCost: 0m,
            ItemsTotal: 1,
            ItemsPassed: 1,
            ItemsExcluded: 0,
            ItemsErrored: 0);
        var slow = fast with { AvgLatencyMs = 90000 };

        // Latency left the composite in scorer version 2: with the old 8000 ms normaliser it was 0
        // in nearly every real run, so the only visible movement between iterations was latency
        // noise on a bit-identical tool accuracy.
        TurnEvalScorer.ComputeComposite(fast).ShouldBe(1.0, Precision);
        TurnEvalScorer.ComputeComposite(slow).ShouldBe(TurnEvalScorer.ComputeComposite(fast), Precision);
    }

    [Test]
    public void ComputeComposite_NoDimensionMeasured_IsZero()
    {
        var dimensions = new TurnEvalDimensions(
            ToolAccuracy: null,
            SlotAccuracy: null,
            NoToolAccuracy: null,
            NameResolutionAccuracy: null,
            AvgLatencyMs: 1000,
            TotalCost: 0m,
            ItemsTotal: 0,
            ItemsPassed: 0,
            ItemsExcluded: 0,
            ItemsErrored: 0);

        TurnEvalScorer.ComputeComposite(dimensions).ShouldBe(0.0, Precision);
    }

    [Test]
    public void Aggregate_RecipeItems_FormTheirOwnWeightedDimension()
    {
        var hit = TurnEvalScorer.ScoreItem(RecipeItem("onboard-employee"), RecipeReplay(forcedRecipe: "onboard-employee"));
        var miss = TurnEvalScorer.ScoreItem(RecipeItem("onboard-employee"), RecipeReplay(forcedRecipe: "offboard-employee"));

        var dimensions = TurnEvalScorer.Aggregate([hit, miss]);

        dimensions.RecipeAccuracy.ShouldBe(0.5);
        dimensions.ToolAccuracy.ShouldBeNull();
        dimensions.NoToolAccuracy.ShouldBeNull();

        // Recipe is the only measured dimension, so the composite IS the recipe accuracy. In scorer
        // version 1 recipe items carried weight 0 while still counting towards the pass rate, so
        // composite and pass rate scored different populations.
        TurnEvalScorer.ComputeComposite(dimensions).ShouldBe(0.5, Precision);
    }

    [Test]
    public void ComputeComposite_RecipeDimension_CarriesItsWeightNextToTool()
    {
        var withRecipe = new TurnEvalDimensions(
            ToolAccuracy: 1.0,
            SlotAccuracy: null,
            NoToolAccuracy: null,
            NameResolutionAccuracy: null,
            AvgLatencyMs: 0,
            TotalCost: 0m,
            ItemsTotal: 2,
            ItemsPassed: 1,
            ItemsExcluded: 0,
            ItemsErrored: 0,
            RecipeAccuracy: 0.0);

        // tool 0.45*1.0 + recipe 0.10*0.0 over weight 0.55.
        TurnEvalScorer.ComputeComposite(withRecipe).ShouldBe(0.45 / 0.55, Precision);
    }

    private static TurnGoldsetItem RecipeItem(string recipe) => new()
    {
        Id = $"recipe-{recipe}",
        Message = "do the thing",
        ExpectedRecipe = recipe
    };

    private static TurnReplayResult RecipeReplay(string forcedRecipe) => new()
    {
        Success = true,
        RecipeWouldForce = true,
        ForcedRecipeName = forcedRecipe
    };

    private static List<TurnEvalItemResult> MixedItems()
    {
        return new List<TurnEvalItemResult>
        {
            new()
            {
                ExpectedTool = ToolName,
                ToolHit = true,
                SlotScore = 1.0,
                NameSlotsEvaluated = 2,
                NameSlotsResolved = 1,
                Passed = true,
                LatencyMs = 1000,
                Cost = 0.01m
            },
            new()
            {
                ExpectedTool = ToolName,
                ToolHit = false,
                LatencyMs = 2000,
                Cost = 0.01m
            },
            new()
            {
                ExpectedTool = null,
                NoToolCorrect = true,
                Passed = true,
                LatencyMs = 3000,
                Cost = 0.01m
            },
            new()
            {
                ExpectedTool = ToolName,
                ToolHit = true,
                SlotScore = 1.0,
                Excluded = true,
                RecipeWouldForce = true,
                LatencyMs = 500,
                Cost = 0.01m
            },
            new()
            {
                ExpectedTool = ToolName,
                ToolHit = false,
                Errored = true,
                LatencyMs = 9999,
                Cost = 0.01m
            }
        };
    }

    private static Dictionary<string, object> JsonParameters(string json)
    {
        var elements = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        return elements.ToDictionary(p => p.Key, p => (object)p.Value);
    }

    private static TurnGoldsetItem ToolItem(string tool, params TurnGoldsetSlot[] slots)
    {
        return new TurnGoldsetItem
        {
            Id = "item-1",
            Message = "test message",
            ExpectedTool = tool,
            ExpectedSlots = slots.ToList()
        };
    }

    private static TurnGoldsetItem NoToolItem()
    {
        return new TurnGoldsetItem
        {
            Id = "item-1",
            Message = "hello there"
        };
    }

    private static TurnReplayResult SuccessReplay(string? tool, Dictionary<string, object>? parameters = null)
    {
        return new TurnReplayResult
        {
            Success = true,
            ChosenTool = tool,
            ToolParameters = parameters ?? new Dictionary<string, object>()
        };
    }

    private static TurnGoldsetSlot ExactSlot(string name, string value) =>
        new() { Name = name, Match = SlotMatchMode.Exact, Value = value };

    private static TurnGoldsetSlot ContainsSlot(string name, string value) =>
        new() { Name = name, Match = SlotMatchMode.Contains, Value = value };

    private static TurnGoldsetSlot IgnoreSlot(string name) =>
        new() { Name = name, Match = SlotMatchMode.Ignore };

    private static TurnGoldsetSlot ResolvedSlot(string name, int idNumber) =>
        new() { Name = name, Match = SlotMatchMode.ResolvedEntityId, Entity = new ExpectedEntityRef("client", idNumber) };
}
