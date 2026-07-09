// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerRecipeExclusionTests
{
    [Test]
    public void ScoreItem_EngineRecipeWouldTrigger_ExcludesItemFromAggregate()
    {
        var item = ToolItem("create_employee");
        var replay = SuccessfulReplay("get_current_time");
        replay.EngineRecipeWouldTrigger = true;

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.Excluded.ShouldBeTrue();
        result.EngineRecipeWouldTrigger.ShouldBeTrue();
        result.Passed.ShouldBeFalse();

        var dimensions = TurnEvalScorer.Aggregate([result]);
        dimensions.ItemsExcluded.ShouldBe(1);
        dimensions.ToolAccuracy.ShouldBeNull();
    }

    [Test]
    public void ScoreItem_ExpectedToolInToolset_ReportsAvailable()
    {
        var item = ToolItem("add_client_note");
        var replay = SuccessfulReplay("search_employees");
        replay.AvailableToolNames = ["search_employees", "ADD_CLIENT_NOTE"];

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ExpectedToolAvailable.ShouldBe(true);
    }

    [Test]
    public void ScoreItem_ExpectedToolMissingFromToolset_ReportsUnavailable()
    {
        var item = ToolItem("add_client_note");
        var replay = SuccessfulReplay("search_employees");
        replay.AvailableToolNames = ["search_employees", "navigate_to"];

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ExpectedToolAvailable.ShouldBe(false);
    }

    [Test]
    public void ScoreItem_AlternativeToolInToolset_CountsAsAvailable()
    {
        var item = ToolItem("add_client_phone");
        item.AlternativeTools = ["update_client"];
        var replay = SuccessfulReplay("update_client");
        replay.AvailableToolNames = ["update_client"];

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ExpectedToolAvailable.ShouldBe(true);
        result.ToolHit.ShouldBe(true);
    }

    [Test]
    public void ScoreItem_NoToolNamesReported_LeavesAvailabilityUnknown()
    {
        var item = ToolItem("add_client_note");
        var replay = SuccessfulReplay("add_client_note");

        var result = TurnEvalScorer.ScoreItem(item, replay);

        result.ExpectedToolAvailable.ShouldBeNull();
    }

    private static TurnGoldsetItem ToolItem(string expectedTool) => new()
    {
        Id = "test-item",
        Message = "message",
        ExpectedTool = expectedTool
    };

    private static TurnReplayResult SuccessfulReplay(string chosenTool) => new()
    {
        Success = true,
        ChosenTool = chosenTool
    };
}
