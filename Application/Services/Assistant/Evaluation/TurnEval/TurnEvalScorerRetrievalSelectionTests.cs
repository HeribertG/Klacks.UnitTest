// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the split of ToolAccuracy into its two causes. A miss because the expected tool was never in the
/// toolset is a retrieval problem; a miss although it was there is a selection problem, and the two are
/// fixed in different places. SelectionHit is therefore measured only over the items where retrieval
/// succeeded - scoring it over items the model never saw the tool for would blame the model for the
/// index. The file also pins the replay temperature, because a sampled eval is not a measurement.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerRetrievalSelectionTests
{
    private const string ExpectedTool = "add_client_note";
    private const string OtherTool = "search_employees";
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string ReplayServiceRelativePath =
        "Application/Services/Assistant/Evaluation/TurnEval/TurnReplayService.cs";

    [Test]
    public void ExpectedToolOfferedAndChosen_IsARetrievalHitAndASelectionHit()
    {
        var result = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(ExpectedTool, [OtherTool, ExpectedTool]));

        result.RetrievalHit.ShouldBe(true);
        result.SelectionHit.ShouldBe(true);
    }

    [Test]
    public void ExpectedToolOfferedButNotChosen_IsARetrievalHitAndASelectionMiss()
    {
        var result = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(OtherTool, [OtherTool, ExpectedTool]));

        result.RetrievalHit.ShouldBe(true);
        result.SelectionHit.ShouldBe(false);
    }

    // The model never saw the tool, so its choice says nothing about selection quality.
    [Test]
    public void ExpectedToolNotOffered_IsARetrievalMissAndSelectionIsNotMeasured()
    {
        var result = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(OtherTool, [OtherTool, "navigate_to"]));

        result.RetrievalHit.ShouldBe(false);
        result.SelectionHit.ShouldBeNull();
    }

    [Test]
    public void NoToolsetReported_LeavesBothVerdictsUnmeasured()
    {
        var result = TurnEvalScorer.ScoreItem(ToolItem(), Replay(ExpectedTool, []));

        result.RetrievalHit.ShouldBeNull();
        result.SelectionHit.ShouldBeNull();
    }

    // A provider timeout still reports the assembled toolset, so retrieval stays measurable while the
    // model never answered. Booking that as a selection miss would file an outage as a model mistake.
    [Test]
    public void AnErroredReplay_LeavesSelectionUnmeasuredAndDoesNotLowerTheAggregate()
    {
        var measured = TurnEvalScorer.ScoreItem(ToolItem(), Replay(ExpectedTool, [ExpectedTool]));
        var errored = TurnEvalScorer.ScoreItem(ToolItem(), FailedReplay([OtherTool, ExpectedTool]));

        errored.Errored.ShouldBeTrue();
        errored.SelectionHit.ShouldBeNull();
        TurnEvalScorer.Aggregate([measured, errored]).SelectionHit.ShouldBe(1.0);
    }

    // A recipe hijacks the turn before the model ever chooses, which is why the item is excluded from
    // every dimension - the selection verdict included.
    [Test]
    public void ARecipeExcludedToolItem_LeavesSelectionUnmeasured()
    {
        var replay = Replay(OtherTool, [OtherTool, ExpectedTool]);
        replay.RecipeWouldForce = true;

        var result = TurnEvalScorer.ScoreItem(ToolItem(), replay);

        result.Excluded.ShouldBeTrue();
        result.SelectionHit.ShouldBeNull();
    }

    [Test]
    public void AnAlternativeTool_CountsForBothVerdicts()
    {
        var item = ToolItem();
        item.AlternativeTools = ["update_client"];

        var result = TurnEvalScorer.ScoreItem(item, Replay("update_client", ["update_client"]));

        result.RetrievalHit.ShouldBe(true);
        result.SelectionHit.ShouldBe(true);
    }

    // The legacy name stays as an alias so the integration scorecard's MISS line keeps working.
    [Test]
    public void ExpectedToolAvailable_IsTheSameVerdictAsRetrievalHit()
    {
        var result = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(OtherTool, [OtherTool, ExpectedTool]));

        result.ExpectedToolAvailable.ShouldBe(result.RetrievalHit);
    }

    [Test]
    public void Aggregate_ReportsRetrievalOverAllToolItemsAndSelectionOverTheRetrievedOnes()
    {
        var offeredAndChosen = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(ExpectedTool, [ExpectedTool]));
        var offeredNotChosen = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(OtherTool, [OtherTool, ExpectedTool]));
        var notOffered = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(OtherTool, [OtherTool]));

        var dimensions = TurnEvalScorer.Aggregate([offeredAndChosen, offeredNotChosen, notOffered]);

        dimensions.RetrievalHit.ShouldBe(2.0 / 3.0);
        dimensions.SelectionHit.ShouldBe(0.5);
    }

    [Test]
    public void Aggregate_WithoutAnyToolItem_LeavesBothDimensionsUnmeasured()
    {
        var noToolItem = TurnEvalScorer.ScoreItem(
            new TurnGoldsetItem { Id = "ts-001", Message = "Hallo" },
            Replay(null, []));

        var dimensions = TurnEvalScorer.Aggregate([noToolItem]);

        dimensions.RetrievalHit.ShouldBeNull();
        dimensions.SelectionHit.ShouldBeNull();
    }

    // The two new dimensions are diagnostics, not quality weights - adding them must not silently
    // move every historical composite.
    [Test]
    public void TheNewDimensions_DoNotChangeTheComposite()
    {
        var offeredAndChosen = TurnEvalScorer.ScoreItem(
            ToolItem(), Replay(ExpectedTool, [ExpectedTool]));
        var dimensions = TurnEvalScorer.Aggregate([offeredAndChosen]);

        var withoutNewDimensions = dimensions with { RetrievalHit = null, SelectionHit = null };

        TurnEvalScorer.ComputeComposite(dimensions)
            .ShouldBe(TurnEvalScorer.ComputeComposite(withoutNewDimensions));
    }

    // Two new per-item verdicts plus a changed replay temperature: runs before and after are not
    // comparable, and the baseline lookup filters on this number.
    [Test]
    public void ScorerVersion_IsBumpedSoOlderRunsAreNeverUsedAsABaseline()
    {
        TurnEvalScorer.ScorerVersion.ShouldBe(4);
    }

    [Test]
    public void ReplayTemperature_IsZero()
    {
        TurnEvalDefaults.ReplayTemperature.ShouldBe(0.0);
    }

    // A source scan, because the value that matters is an assignment in a method body: reflection
    // would only see the request type, never which number was put into it.
    [Test]
    public void TurnReplayService_UsesTheSharedZeroTemperatureConstant()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), ApiProjectDirectory, ReplayServiceRelativePath));

        source.ShouldContain("Temperature = TurnEvalDefaults.ReplayTemperature");
        source.ShouldNotContain("ReplayTemperature = 0.7");
    }

    private static TurnGoldsetItem ToolItem() =>
        new() { Id = "ts-011", Message = "Notiz hinzufuegen", Locale = "de", ExpectedTool = ExpectedTool };

    private static TurnReplayResult Replay(string? chosenTool, List<string> availableToolNames) =>
        new()
        {
            Success = true,
            ChosenTool = chosenTool,
            AvailableToolNames = availableToolNames
        };

    private static TurnReplayResult FailedReplay(List<string> availableToolNames) =>
        new()
        {
            Success = false,
            Error = "provider timeout",
            AvailableToolNames = availableToolNames
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root with Klacks.Api not found.");
    }
}
