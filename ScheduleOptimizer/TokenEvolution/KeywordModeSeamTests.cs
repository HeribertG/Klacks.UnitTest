// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Fitness;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords;
using NUnit.Framework;
using Shouldly;
using static Klacks.UnitTest.ScheduleOptimizer.Constraints.Keywords.KeywordWishTestData;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

/// <summary>
/// Wizard 1 seams of the keyword mode. Hard keeps today's vetoes (pinned by the existing Stage-0 / filter / checker
/// tests); Soft turns every keyword restriction into a wish: no Stage-0 veto, a Stage-1 (round-2) verdict in the auction,
/// a veto only below the last rung of the coverage escalation, no legality violation and a plannable day for MaxPossible.
/// </summary>
[TestFixture]
public class KeywordModeSeamTests
{
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static CoreWizardContext FreeOnMonday(KeywordMode mode)
        => Context(
            [Agent("A")],
            [Slot(ShiftId, Monday, Early)],
            [new CoreScheduleCommand("A", Monday, ScheduleCommandKeyword.Free)],
            mode);

    [Test]
    public void Stage0_SoftMode_DoesNotVetoAFreeDay()
    {
        new Stage0HardConstraintChecker().Check(Agent("A"), Slot(ShiftId, Monday, Early), [], FreeOnMonday(KeywordMode.Soft))
            .ShouldBeNull();
    }

    [Test]
    public void Stage0_HardMode_StillVetoesAFreeDay()
    {
        new Stage0HardConstraintChecker().Check(Agent("A"), Slot(ShiftId, Monday, Early), [], FreeOnMonday(KeywordMode.Hard))!
            .RuleName.ShouldBe(Stage0RuleNames.KeywordFree);
    }

    [Test]
    public void Stage1_SoftMode_ReportsTheBrokenWishAsARoundTwoVerdict()
    {
        var verdict = new Stage1SoftConstraintChecker().Check(Agent("A"), Slot(ShiftId, Monday, Early), [], FreeOnMonday(KeywordMode.Soft));

        verdict.ShouldNotBeNull();
        verdict!.Stage.ShouldBe(1);
        verdict.RuleName.ShouldBe(Stage0RuleNames.KeywordFree);
    }

    [Test]
    public void Stage1_HardMode_IgnoresKeywords()
    {
        new Stage1SoftConstraintChecker().Check(Agent("A"), Slot(ShiftId, Monday, Early), [], FreeOnMonday(KeywordMode.Hard))
            .ShouldBeNull();
    }

    [TestCase(SlotRelaxation.None, false)]
    [TestCase(SlotRelaxation.RestDaysOnly, false)]
    [TestCase(SlotRelaxation.All, true)]
    public void SlotFilter_SoftMode_LiftsTheWishOnlyOnTheLastRung(SlotRelaxation rung, bool valid)
    {
        var context = FreeOnMonday(KeywordMode.Soft);

        SlotConstraintFilter.IsValidAssignment(
                Agent("A"), Monday, ShiftTypeInference.EarlyIndex, ShiftId, 8, context, [], relaxation: rung)
            .ShouldBe(valid);
        SlotConstraintFilter.ViolatesAbsoluteVeto(
                Agent("A"), Monday, ShiftTypeInference.EarlyIndex, ShiftId, context, [], null, null)
            .ShouldBeFalse();
    }

    [TestCase(SlotRelaxation.None)]
    [TestCase(SlotRelaxation.All)]
    public void SlotFilter_HardMode_VetoesOnEveryRung(SlotRelaxation rung)
    {
        SlotConstraintFilter.IsValidAssignment(
                Agent("A"), Monday, ShiftTypeInference.EarlyIndex, ShiftId, 8, FreeOnMonday(KeywordMode.Hard), [], relaxation: rung)
            .ShouldBeFalse();
    }

    [TestCase(KeywordMode.Hard, 1)]
    [TestCase(KeywordMode.Soft, 0)]
    public void PlanChecker_CountsAKeywordViolationAsLegalityOnlyInHardMode(KeywordMode mode, int expected)
    {
        var assignments = new[] { AssignmentView.FromToken(Token("A", ShiftId, Monday, Early)) };

        new PlanConstraintChecker().Check(assignments, FreeOnMonday(mode))
            .Count(v => v.Kind == ViolationKind.PerDayKeywordViolation)
            .ShouldBe(expected);
    }

    [TestCase(KeywordMode.Hard, 0d)]
    [TestCase(KeywordMode.Soft, 8d)]
    public void MaxPossible_TreatsAKeywordDayAsPlannableOnlyInSoftMode(KeywordMode mode, double expected)
    {
        new MaxPossibleCalculator().ComputeForAll(FreeOnMonday(mode))["A"].ShouldBe(expected);
    }
}
