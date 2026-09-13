// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the targeted holdout replay. It answers one question about a sharpened description: did any
/// holdout item that used to select correctly stop doing so. Only holdout items count, only items that
/// involve the sharpened skill are replayed, only previously passing items can regress, and the number of
/// replays is capped because every one of them is a paid provider call.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetHoldoutReplayGateTests
{
    private const string Sharpened = "list_clients";
    private const string Confused = "revenue_per_client";
    private const string Model = "deepseek-v4-pro";

    private static readonly Guid RunId = Guid.NewGuid();

    private static readonly string[] HoldoutIds =
        [.. Enumerable.Range(1, 4000).Select(i => $"ts-{i:D4}")
            .Where(GoldsetPartitioner.IsHoldout).Take(40)];

    private static readonly string TrainId =
        Enumerable.Range(1, 4000).Select(i => $"ts-{i:D4}").First(GoldsetPartitioner.IsTrain);

    private IEvalRunRepository _evalRuns = null!;
    private IEvalRunItemRepository _evalRunItems = null!;
    private ITurnGoldsetLoader _goldsetLoader = null!;
    private ITurnReplayService _replayService = null!;
    private GoldsetHoldoutReplayGate _gate = null!;

    [SetUp]
    public void SetUp()
    {
        _evalRuns = Substitute.For<IEvalRunRepository>();
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Id = RunId, Model = Model });

        _evalRunItems = Substitute.For<IEvalRunItemRepository>();
        _goldsetLoader = Substitute.For<ITurnGoldsetLoader>();
        _replayService = Substitute.For<ITurnReplayService>();

        _gate = new GoldsetHoldoutReplayGate(
            _evalRuns, _evalRunItems, _goldsetLoader, _replayService,
            NullLogger<GoldsetHoldoutReplayGate>.Instance);
    }

    [Test]
    public async Task APreviouslyPassingHoldoutItemThatNowFails_IsARegression()
    {
        GivenRows(Row(HoldoutIds[0], expectedTool: Sharpened, selectionHit: true));
        GivenGoldset(Item(HoldoutIds[0], Sharpened));
        GivenReplayChoosing(Confused);

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeTrue();
        verdict.Regressions.Count.ShouldBe(1);
        verdict.Regressions[0].ShouldContain(HoldoutIds[0]);
    }

    [Test]
    public async Task APreviouslyPassingHoldoutItemThatStillPasses_IsNoRegression()
    {
        GivenRows(Row(HoldoutIds[0], expectedTool: Sharpened, selectionHit: true));
        GivenGoldset(Item(HoldoutIds[0], Sharpened));
        GivenReplayChoosing(Sharpened);

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeTrue();
        verdict.Regressions.ShouldBeEmpty();
    }

    // An item that was already failing is not this proposal's doing - the same baseline rule the
    // golden-case gate applies.
    [Test]
    public async Task AnItemThatWasAlreadyFailing_CannotRegress()
    {
        GivenRows(Row(HoldoutIds[0], expectedTool: Sharpened, selectionHit: false));
        GivenGoldset(Item(HoldoutIds[0], Sharpened));
        GivenReplayChoosing(Confused);

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Regressions.ShouldBeEmpty();
    }

    [Test]
    public async Task TrainItems_AreNeverReplayed()
    {
        GivenRows(Row(TrainId, expectedTool: Sharpened, selectionHit: true));
        GivenGoldset(Item(TrainId, Sharpened));

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeFalse();
        await _replayService.DidNotReceiveWithAnyArgs().ReplayAsync(default!, default!, default!, default!);
    }

    // The items the sharpened skill currently steals are exactly the ones the narrowing is aimed at,
    // so they have to be in the replay set even though they expect a different tool.
    [Test]
    public async Task ItemsTheSharpenedSkillWronglyWon_AreAlsoReplayed()
    {
        GivenRows(Row(HoldoutIds[0], expectedTool: Confused, chosenTool: Sharpened, selectionHit: false));
        GivenGoldset(Item(HoldoutIds[0], Confused));
        GivenReplayChoosing(Confused);

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeTrue();
        await _replayService.Received(1).ReplayAsync(
            Arg.Any<TurnGoldsetItem>(), Model, Arg.Any<string>(), Arg.Any<List<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ItemsOfUnrelatedSkills_AreNotReplayed()
    {
        GivenRows(Row(HoldoutIds[0], expectedTool: "navigate_to", chosenTool: "navigate_to", selectionHit: true));
        GivenGoldset(Item(HoldoutIds[0], "navigate_to"));

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeFalse();
        await _replayService.DidNotReceiveWithAnyArgs().ReplayAsync(default!, default!, default!, default!);
    }

    [Test]
    public async Task TheNumberOfReplays_IsCapped()
    {
        var rows = HoldoutIds
            .Select(id => Row(id, expectedTool: Sharpened, selectionHit: true))
            .ToArray();
        GivenRows(rows);
        GivenGoldset([.. HoldoutIds.Select(id => Item(id, Sharpened))]);
        GivenReplayChoosing(Sharpened);

        await _gate.EvaluateAsync(Sharpened);

        await _replayService.Received(SkillLearningDefaults.MaxTargetedHoldoutReplaysPerProposal)
            .ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Model, Arg.Any<string>(), Arg.Any<List<string>>(),
                Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutAFullRun_TheGateReportsThatNothingWasMeasured()
    {
        _evalRuns.GetLatestFullRunAsync(
                TurnEvalDefaults.DefaultGoldset, TurnEvalScorer.ScorerVersion, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeFalse();
        verdict.Regressions.ShouldBeEmpty();
    }

    // A replay the provider never answered is not evidence about the description. Scoring it would turn
    // a timeout into a regression, and blocked_regression is terminal: the proposal would carry a reason
    // that never happened. With no answered replay at all the gate has measured nothing.
    [Test]
    public async Task AReplayThatDidNotAnswer_IsNoRegressionAndMeasuresNothing()
    {
        GivenRows(Row(HoldoutIds[0], expectedTool: Sharpened, selectionHit: true));
        GivenGoldset(Item(HoldoutIds[0], Sharpened));
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(new TurnReplayResult { Success = false, Error = "the provider timed out" });

        var verdict = await _gate.EvaluateAsync(Sharpened);

        verdict.Measured.ShouldBeFalse();
        verdict.Regressions.ShouldBeEmpty();
    }

    private void GivenRows(params EvalRunItem[] rows) =>
        _evalRunItems.ListByRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(rows);

    private void GivenGoldset(params TurnGoldsetItem[] items) =>
        _goldsetLoader.LoadAsync(TurnEvalDefaults.DefaultGoldset, Arg.Any<CancellationToken>())
            .Returns(items);

    private void GivenReplayChoosing(string tool) =>
        _replayService.ReplayAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(new TurnReplayResult
            {
                Success = true,
                ChosenTool = tool,
                AvailableToolNames = [Sharpened, Confused, "navigate_to"]
            });

    private static EvalRunItem Row(
        string itemId, string expectedTool, bool selectionHit, string? chosenTool = null) => new()
    {
        Id = Guid.NewGuid(),
        EvalRunId = RunId,
        ItemId = itemId,
        Locale = "de",
        ExpectedTool = expectedTool,
        ChosenTool = chosenTool ?? (selectionHit ? expectedTool : "navigate_to"),
        RetrievalHit = true,
        SelectionHit = selectionHit,
        Passed = selectionHit
    };

    private static TurnGoldsetItem Item(string itemId, string expectedTool) => new()
    {
        Id = itemId,
        Message = "Zeige mir etwas",
        Locale = "de",
        ExpectedTool = expectedTool
    };
}
