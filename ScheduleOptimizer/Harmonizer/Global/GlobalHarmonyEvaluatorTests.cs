// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;

/// <summary>
/// The second instance measures the harmony BETWEEN the rows: a canon (same pattern, shifted) is perfect rhythm, an
/// absence never breaks the rhythm, and fairness compares shares of each row's own working days.
/// </summary>
[TestFixture]
public sealed class GlobalHarmonyEvaluatorTests
{
    private const string Pattern = "EEE__LLL__NNN__";
    private readonly GlobalHarmonyEvaluator _evaluator = new();

    private static string Shifted(int offset) => Pattern[^offset..] + Pattern[..^offset];

    [Test]
    public void Canon_HasPerfectRhythm()
    {
        var bitmap = HarmonyTestPlans.Bitmap(Pattern, Shifted(5), Shifted(10));

        _evaluator.Evaluate(bitmap).Rhythm.ShouldBe(1d, 1e-9);
    }

    [Test]
    public void BrokenRow_LowersTheRhythm()
    {
        var canon = _evaluator.Evaluate(HarmonyTestPlans.Bitmap(Pattern, Shifted(5), Shifted(10)));
        var broken = _evaluator.Evaluate(HarmonyTestPlans.Bitmap(Pattern, Shifted(5), "E_L_N_E_L_N___E"));

        broken.Rhythm.ShouldBeLessThan(canon.Rhythm);
    }

    [Test]
    public void Absence_DoesNotBreakTheRhythm()
    {
        var withAbsence = Shifted(10)[..6] + "BBB" + Shifted(10)[9..];

        var score = _evaluator.Evaluate(HarmonyTestPlans.Bitmap(Pattern, Shifted(5), withAbsence));

        score.Rhythm.ShouldBe(1d, 1e-9);
    }

    [Test]
    public void OneRowWithAllNights_IsUnfair()
    {
        var fair = _evaluator.Evaluate(HarmonyTestPlans.Bitmap("EELLNN_", "LLNNEE_", "NNEELL_"));
        var unfair = _evaluator.Evaluate(HarmonyTestPlans.Bitmap("EELLEE_", "LLEELL_", "NNNNNN_"));

        fair.Fairness.ShouldBe(1d, 1e-9);
        unfair.Fairness.ShouldBeLessThan(fair.Fairness);
    }

    [Test]
    public void PartTimeRow_WithTheSameMix_IsNotUnfair()
    {
        var score = _evaluator.Evaluate(HarmonyTestPlans.Bitmap("ELN_E__LN", "ELN______"));

        score.Fairness.ShouldBe(1d, 1e-9);
    }

    [Test]
    public void FewerThanTwoWorkingRows_IsNeutral()
    {
        var score = _evaluator.Evaluate(HarmonyTestPlans.Bitmap("EELLNN", "______"));

        score.Rhythm.ShouldBe(1d);
        score.Fairness.ShouldBe(1d);
    }
}
