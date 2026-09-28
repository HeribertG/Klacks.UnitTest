// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The paired verdict. The same items are replayed with the old and the new description in one run, so
/// provider noise hits both sides: only a flip between the two replays counts. A proposal passes when fixed
/// minus regressed reaches the minimum and no holdout item regressed; an item either side left unanswered is
/// not evidence at all.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetGateJudgeTests
{
    private static readonly GoldsetItemRef Holdout = new(TurnEvalDefaults.DefaultGoldset, "ts-h");
    private static readonly GoldsetItemRef Train = new(TurnEvalDefaults.DefaultGoldset, "ts-t");
    private static readonly GoldsetItemRef SecondTrain = new(TurnEvalDefaults.ParaphraseGoldset, "para-ts-t-1");

    private static readonly GoldsetReplayPlan Plan =
        new(Guid.NewGuid(), "deepseek-v4-pro", 6, [Holdout], [Train, SecondTrain]);

    private static IReadOnlyDictionary<GoldsetItemRef, bool?> Replay(bool? holdout, bool? train, bool? secondTrain) =>
        new Dictionary<GoldsetItemRef, bool?> { [Holdout] = holdout, [Train] = train, [SecondTrain] = secondTrain };

    [Test]
    public void AFixedTrainMissWithoutRegression_Passes()
    {
        var outcome = GoldsetGateJudge.Judge(Plan, Replay(true, false, false), Replay(true, true, false), 1);

        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.Passed);
        outcome.NetGain.ShouldBe(1);
        outcome.TrainFixed.ShouldHaveSingleItem().ShouldBe("ts-t");
    }

    [Test]
    public void AFixedHoldoutMiss_CountsTowardsTheNetGain()
    {
        var outcome = GoldsetGateJudge.Judge(Plan, Replay(false, false, false), Replay(true, false, false), 1);

        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.Passed);
        outcome.NetGain.ShouldBe(1);
        outcome.HoldoutFixed.ShouldHaveSingleItem().ShouldBe("ts-h");
    }

    [Test]
    public void NothingFixed_IsNoNetGain()
    {
        GoldsetGateJudge.Judge(Plan, Replay(true, false, false), Replay(true, false, false), 1)
            .Verdict.ShouldBe(GoldsetGateVerdicts.NoNetGain);
    }

    [Test]
    public void AHoldoutRegression_BlocksEvenWithAGain()
    {
        var outcome = GoldsetGateJudge.Judge(Plan, Replay(true, false, false), Replay(false, true, true), 1);

        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.BlockedRegression);
        outcome.HoldoutRegressions.ShouldHaveSingleItem().ShouldBe("ts-h");
    }

    [Test]
    public void AHoldoutItemMissedWithTheOldDescriptionToo_IsNoRegression()
    {
        GoldsetGateJudge.Judge(Plan, Replay(false, false, false), Replay(false, true, false), 1)
            .Verdict.ShouldBe(GoldsetGateVerdicts.Passed);
    }

    [Test]
    public void ATrainItemThatGotWorse_CostsNetGain()
    {
        var outcome = GoldsetGateJudge.Judge(Plan, Replay(true, false, true), Replay(true, true, false), 1);

        outcome.NetGain.ShouldBe(0);
        outcome.TrainRegressions.ShouldHaveSingleItem().ShouldBe("para-ts-t-1");
        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.NoNetGain);
    }

    [Test]
    public void AnItemUnansweredOnEitherSide_IsNotCounted()
    {
        var outcome = GoldsetGateJudge.Judge(Plan, Replay(true, null, false), Replay(true, true, true), 1);

        outcome.HoldoutMeasured.ShouldBe(1);
        outcome.TrainMeasured.ShouldBe(1);
        outcome.TrainFixed.ShouldHaveSingleItem().ShouldBe("para-ts-t-1");
        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.Passed);
    }

    // The German holdout and the translated holdout protect different users: when every planned item of one
    // holdout goldset went unanswered, the other goldset's measured items cannot stand in for it.
    [Test]
    public void OneHoldoutGoldsetEntirelyUnanswered_IsNotMeasuredEvenIfTheOtherWasMeasured()
    {
        var translated = new GoldsetItemRef(TurnEvalDefaults.I18nGoldset, "i18n-ja--ts-h");
        var plan = new GoldsetReplayPlan(Guid.NewGuid(), "deepseek-flash", 6, [Holdout, translated], [Train]);
        var before = new Dictionary<GoldsetItemRef, bool?> { [Holdout] = null, [translated] = true, [Train] = false };
        var after = new Dictionary<GoldsetItemRef, bool?> { [Holdout] = true, [translated] = true, [Train] = true };

        GoldsetGateJudge.Judge(plan, before, after, 1).Verdict.ShouldBe(GoldsetGateVerdicts.NotMeasured);
    }

    [Test]
    public void BothHoldoutGoldsetsMeasured_AreJudgedTogether()
    {
        var translated = new GoldsetItemRef(TurnEvalDefaults.I18nGoldset, "i18n-ja--ts-h");
        var plan = new GoldsetReplayPlan(Guid.NewGuid(), "deepseek-flash", 6, [Holdout, translated], [Train]);
        var before = new Dictionary<GoldsetItemRef, bool?> { [Holdout] = true, [translated] = true, [Train] = false };
        var after = new Dictionary<GoldsetItemRef, bool?> { [Holdout] = true, [translated] = false, [Train] = true };

        var outcome = GoldsetGateJudge.Judge(plan, before, after, 1);

        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.BlockedRegression);
        outcome.HoldoutRegressions.ShouldHaveSingleItem().ShouldBe("i18n-ja--ts-h");
    }

    // Without a measured holdout item there is no regression protection, so a train gain alone must not pass
    // a proposal whose holdout items were planned - for example when the provider failed only on those calls.
    [Test]
    public void PlannedHoldoutItemsAllUnanswered_AreNotMeasuredEvenWithATrainGain()
    {
        var outcome = GoldsetGateJudge.Judge(Plan, Replay(null, false, false), Replay(false, true, true), 1);

        outcome.HoldoutMeasured.ShouldBe(0);
        outcome.TrainMeasured.ShouldBe(2);
        outcome.Verdict.ShouldBe(GoldsetGateVerdicts.NotMeasured);
    }

    [Test]
    public void WithoutPlannedHoldoutItems_TheTrainItemsDecideAlone()
    {
        var trainOnlyPlan = Plan with { HoldoutItems = [] };

        GoldsetGateJudge.Judge(trainOnlyPlan, Replay(null, false, false), Replay(null, true, false), 1)
            .Verdict.ShouldBe(GoldsetGateVerdicts.Passed);
    }

    [Test]
    public void NothingAnswered_IsNotMeasured()
    {
        GoldsetGateJudge.Judge(Plan, Replay(null, null, null), Replay(true, true, true), 1)
            .Verdict.ShouldBe(GoldsetGateVerdicts.NotMeasured);
    }

    [Test]
    public void TheMinimumComesFromTheCaller()
    {
        GoldsetGateJudge.Judge(Plan, Replay(true, false, false), Replay(true, true, false), 2)
            .Verdict.ShouldBe(GoldsetGateVerdicts.NoNetGain);
    }
}
