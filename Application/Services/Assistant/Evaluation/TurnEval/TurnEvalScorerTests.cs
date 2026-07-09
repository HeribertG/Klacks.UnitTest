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
    public void ScoreItem_OnlyIgnoreSlots_SlotScoreIsOne()
    {
        var item = ToolItem(ToolName, IgnoreSlot("firstName"));
        var replay = SuccessReplay(ToolName);

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.SlotScore.ShouldBe(1.0);
        result.Passed.ShouldBeTrue();
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

        composite.ShouldBe(0.925, Precision);
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
    public void ComputeComposite_LatencyAboveNormalizer_ClampsToZero()
    {
        var dimensions = new TurnEvalDimensions(
            ToolAccuracy: 1.0,
            SlotAccuracy: 1.0,
            NoToolAccuracy: 1.0,
            NameResolutionAccuracy: 1.0,
            AvgLatencyMs: 16000,
            TotalCost: 0m,
            ItemsTotal: 1,
            ItemsPassed: 1,
            ItemsExcluded: 0,
            ItemsErrored: 0);

        var composite = TurnEvalScorer.ComputeComposite(dimensions);

        composite.ShouldBe(0.9, Precision);
    }

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
