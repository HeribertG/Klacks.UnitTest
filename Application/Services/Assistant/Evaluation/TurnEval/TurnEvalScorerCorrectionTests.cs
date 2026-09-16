// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Scoring of the three graceful-correction verdicts: correctionHit (the corrected turn reached the
/// expected skill), falseRepair (a normal follow-up was treated as a correction) and
/// undoOfferedWhenExpected. Only items that declare a previousTurn are measured at all, and only when
/// the replay succeeded and the item was not excluded by a recipe hijack.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerCorrectionTests
{
    private const string PreviousSkill = "find_customer_candidates";
    private const string ExpectedSkill = "search_employees";
    private const string UndoSkill = "remove_shift_from_group";

    private static TurnGoldsetItem CorrectionItem() => new()
    {
        Id = "cr-test-1",
        Message = "Nein, ich meinte alle Mitarbeitenden.",
        ExpectedTool = ExpectedSkill,
        ExpectsCorrection = true,
        PreviousTurn = new TurnGoldsetPreviousTurn
        {
            Message = "Trag alle Mitarbeitenden in die Gruppe Zürich ein.",
            CalledSkill = PreviousSkill,
            AssistantAnswerExcerpt = "Ich habe nach Kunden gesucht."
        }
    };

    private static TurnReplayResult Replay(
        string? chosenTool,
        bool correctionApplied,
        bool clarification = false,
        string? undoSkill = null,
        bool success = true) => new()
    {
        Success = success,
        ChosenTool = chosenTool,
        AvailableToolNames = [ExpectedSkill],
        CorrectionApplied = correctionApplied,
        CorrectionClarificationOffered = clarification,
        UndoOfferedSkill = undoSkill
    };

    [Test]
    public void CorrectionItem_ReroutedToTheExpectedSkill_IsAHit()
    {
        var result = TurnEvalScorer.ScoreItem(CorrectionItem(), Replay(ExpectedSkill, correctionApplied: true));

        result.CorrectionHit.ShouldBe(true);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void CorrectionItem_NotDetectedAsACorrection_IsAMissEvenWhenTheToolIsRight()
    {
        var result = TurnEvalScorer.ScoreItem(CorrectionItem(), Replay(ExpectedSkill, correctionApplied: false));

        result.CorrectionHit.ShouldBe(false);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ClarificationItem_IsAHitWhenTheClarificationWasOffered()
    {
        var item = CorrectionItem();
        item.ExpectedTool = null;
        item.ExpectsClarification = true;

        var result = TurnEvalScorer.ScoreItem(item, Replay(null, correctionApplied: true, clarification: true));

        result.CorrectionHit.ShouldBe(true);
    }

    [Test]
    public void FollowUpItem_TreatedAsACorrection_IsAFalseRepair()
    {
        var item = CorrectionItem();
        item.ExpectsCorrection = false;

        var result = TurnEvalScorer.ScoreItem(item, Replay(ExpectedSkill, correctionApplied: true));

        result.FalseRepair.ShouldBe(true);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ItemWithoutPreviousTurn_MeasuresNoCorrectionVerdictAtAll()
    {
        var item = CorrectionItem();
        item.PreviousTurn = null;
        item.ExpectsCorrection = false;

        var result = TurnEvalScorer.ScoreItem(item, Replay(ExpectedSkill, correctionApplied: false));

        result.CorrectionHit.ShouldBeNull();
        result.FalseRepair.ShouldBeNull();
    }

    [Test]
    public void ExpectedUndo_IsMeasuredAgainstTheOfferedInverseSkill()
    {
        var item = CorrectionItem();
        item.ExpectedUndoSkill = UndoSkill;

        var hit = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, undoSkill: UndoSkill));
        var miss = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, undoSkill: null));

        hit.UndoOfferedWhenExpected.ShouldBe(true);
        miss.UndoOfferedWhenExpected.ShouldBe(false);
    }

    [Test]
    public void Aggregate_ReportsTheThreeCorrectionDimensions()
    {
        var hit = TurnEvalScorer.ScoreItem(CorrectionItem(), Replay(ExpectedSkill, correctionApplied: true));
        var followUp = CorrectionItem();
        followUp.ExpectsCorrection = false;
        var falseRepair = TurnEvalScorer.ScoreItem(followUp, Replay(ExpectedSkill, correctionApplied: true));

        var dimensions = TurnEvalScorer.Aggregate([hit, falseRepair]);

        dimensions.CorrectionHit.ShouldBe(1.0);
        dimensions.FalseRepairRate.ShouldBe(1.0);
        dimensions.UndoOfferedWhenExpected.ShouldBeNull();
    }

    [Test]
    public void FailedReplay_LeavesAllThreeCorrectionVerdictsUnmeasured()
    {
        var item = CorrectionItem();
        item.ExpectedUndoSkill = UndoSkill;

        var result = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, undoSkill: UndoSkill, success: false));

        result.CorrectionHit.ShouldBeNull();
        result.FalseRepair.ShouldBeNull();
        result.UndoOfferedWhenExpected.ShouldBeNull();
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void ClarificationItem_ToolReachedInsteadOfClarifying_IsAMiss()
    {
        var item = CorrectionItem();
        item.ExpectedTool = null;
        item.ExpectsClarification = true;

        var result = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, clarification: false));

        result.CorrectionHit.ShouldBe(false);
    }

    [Test]
    public void OrdinaryFollowUp_CorrectlyNotRepaired_PassesWithoutTool()
    {
        var item = CorrectionItem();
        item.ExpectedTool = null;
        item.ExpectsCorrection = false;

        var result = TurnEvalScorer.ScoreItem(item, Replay(null, correctionApplied: false));

        result.FalseRepair.ShouldBe(false);
        result.Passed.ShouldBeTrue();
    }

    [Test]
    public void UndoNotOfferedWhenExpected_ForcesTheItemToFail()
    {
        var item = CorrectionItem();
        item.ExpectedUndoSkill = UndoSkill;

        var result = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, undoSkill: null));

        result.UndoOfferedWhenExpected.ShouldBe(false);
        result.Passed.ShouldBeFalse();
    }

    [Test]
    public void UndoComparison_IsCaseInsensitive()
    {
        var item = CorrectionItem();
        item.ExpectedUndoSkill = UndoSkill;

        var result = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, undoSkill: UndoSkill.ToUpperInvariant()));

        result.UndoOfferedWhenExpected.ShouldBe(true);
    }

    [Test]
    public void Aggregate_ReportsTheUndoDimensionWhenAnyItemMeasuresIt()
    {
        var item = CorrectionItem();
        item.ExpectedUndoSkill = UndoSkill;
        var hit = TurnEvalScorer.ScoreItem(
            item, Replay(ExpectedSkill, correctionApplied: true, undoSkill: UndoSkill));

        var dimensions = TurnEvalScorer.Aggregate([hit]);

        dimensions.UndoOfferedWhenExpected.ShouldBe(1.0);
    }
}
