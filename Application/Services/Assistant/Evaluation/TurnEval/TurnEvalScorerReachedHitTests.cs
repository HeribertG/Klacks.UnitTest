// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins ReachedHit next to the unchanged SelectionHit. ReachedHit shares SelectionHit's population,
/// equals it whenever no second step ran, turns a first-step lookup into a hit only when the second
/// step landed on an acceptable tool, and is left unmeasured when that second call failed. The detour
/// rate keeps the rescued lookups visible instead of hiding them inside the better-looking number.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerReachedHitTests
{
    private const string ExpectedTool = "delete_client";
    private const string AlternativeTool = "deactivate_client";
    private const string LookupTool = "search_employees";
    private const string WrongTool = "create_group";

    private static TurnGoldsetItem Item() => new()
    {
        Id = "rh-001",
        Message = "Frau Amstutz bitte ausbuchen.",
        ExpectedTool = ExpectedTool,
        AlternativeTools = [AlternativeTool]
    };

    private static TurnReplayResult Replay(string? firstTool, string? secondTool = null, bool followUp = false,
        bool followUpFailed = false, bool toolOffered = true)
    {
        var replay = new TurnReplayResult
        {
            Success = true,
            ChosenTool = firstTool,
            AvailableToolNames = toolOffered ? [LookupTool, ExpectedTool, WrongTool] : [LookupTool, WrongTool],
            FollowUpAttempted = followUp,
            FollowUpFailed = followUpFailed
        };
        replay.Steps.Add(new TurnReplayStep { Tool = firstTool });
        if (followUp)
        {
            replay.Steps.Add(new TurnReplayStep { Tool = secondTool, Success = !followUpFailed });
        }

        return replay;
    }

    [Test]
    public void FirstStepHit_IsReached_WithoutDetour()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(ExpectedTool));

        result.SelectionHit.ShouldBe(true);
        result.ReachedHit.ShouldBe(true);
        result.ReachedViaFollowUp.ShouldBeFalse();
    }

    [Test]
    public void LookupThenExpectedTool_IsASelectionMissButReached_ViaDetour()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(LookupTool, ExpectedTool, followUp: true));

        result.SelectionHit.ShouldBe(false);
        result.ReachedHit.ShouldBe(true);
        result.ReachedViaFollowUp.ShouldBeTrue();
    }

    [Test]
    public void LookupThenAlternativeTool_IsReached()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(LookupTool, AlternativeTool, followUp: true));

        result.ReachedHit.ShouldBe(true);
    }

    [Test]
    public void LookupThenWrongTool_IsNotReached()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(LookupTool, WrongTool, followUp: true));

        result.ReachedHit.ShouldBe(false);
        result.ReachedViaFollowUp.ShouldBeFalse();
    }

    [Test]
    public void LookupThenNoToolCall_IsNotReached()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(LookupTool, secondTool: null, followUp: true));

        result.ReachedHit.ShouldBe(false);
    }

    [Test]
    public void MissWithoutFollowUp_EqualsSelectionHit()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(WrongTool));

        result.SelectionHit.ShouldBe(false);
        result.ReachedHit.ShouldBe(false);
    }

    [Test]
    public void NoToolCallAtAll_IsNotReached()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(firstTool: null));

        result.ReachedHit.ShouldBe(false);
    }

    [Test]
    public void FailedFollowUp_LeavesReachedUnmeasured_AndSelectionUntouched()
    {
        var result = TurnEvalScorer.ScoreItem(
            Item(), Replay(LookupTool, secondTool: null, followUp: true, followUpFailed: true));

        result.SelectionHit.ShouldBe(false);
        result.ReachedHit.ShouldBeNull();
        result.Errored.ShouldBeFalse();
    }

    [Test]
    public void SelectionUnmeasured_LeavesReachedUnmeasured()
    {
        var result = TurnEvalScorer.ScoreItem(Item(), Replay(LookupTool, toolOffered: false));

        result.SelectionHit.ShouldBeNull();
        result.ReachedHit.ShouldBeNull();
    }

    [Test]
    public void Aggregate_ReportsReachedHitAndTheDetourShare()
    {
        var direct = TurnEvalScorer.ScoreItem(Item(), Replay(ExpectedTool));
        var detour = TurnEvalScorer.ScoreItem(Item(), Replay(LookupTool, ExpectedTool, followUp: true));
        var miss = TurnEvalScorer.ScoreItem(Item(), Replay(WrongTool));
        var unmeasured = TurnEvalScorer.ScoreItem(
            Item(), Replay(LookupTool, secondTool: null, followUp: true, followUpFailed: true));

        var dimensions = TurnEvalScorer.Aggregate([direct, detour, miss, unmeasured]);

        dimensions.SelectionHit.ShouldBe(0.25);
        dimensions.ReachedHit!.Value.ShouldBe(2.0 / 3.0, 0.0001);
        dimensions.LookupDetourRate.ShouldBe(0.5);
    }

    [Test]
    public void Aggregate_WithoutAnyReachedItem_ReportsNoDetourRate()
    {
        var miss = TurnEvalScorer.ScoreItem(Item(), Replay(WrongTool));

        TurnEvalScorer.Aggregate([miss]).LookupDetourRate.ShouldBeNull();
    }
}
