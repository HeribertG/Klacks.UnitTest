// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;

/// <summary>
/// The Wizard 2 guard: the rules keep their veto, and on top of them no swap may take the global harmony below the
/// harmony of the start plan. The check leaves the plan exactly as it found it.
/// </summary>
[TestFixture]
public sealed class HarmonyGuardReplaceValidatorTests
{
    private const string Pattern = "EEE__LLL__NNN__";

    private static string Shifted(int offset) => Pattern[^offset..] + Pattern[..^offset];

    private sealed class AllowAll : IReplaceValidator
    {
        public bool IsValid(HarmonyBitmap bitmap, ReplaceMove move) => true;
    }

    private sealed class DenyAll : IReplaceValidator
    {
        public bool IsValid(HarmonyBitmap bitmap, ReplaceMove move) => false;
    }

    [Test]
    public void SwapThatBreaksTheCanon_IsRejected()
    {
        // Arrange: a perfect canon; swapping day 0 of row 0 (early) with row 1 breaks both rows' rhythm.
        var bitmap = HarmonyTestPlans.Bitmap(Pattern, Shifted(5), Shifted(10));
        var guard = HarmonyGuardReplaceValidator.ForStart(new AllowAll(), bitmap);
        var move = new ReplaceMove(0, 1, 0);
        bitmap.GetCell(0, 0).Symbol.ShouldNotBe(bitmap.GetCell(1, 0).Symbol);

        // Act
        var valid = guard.IsValid(bitmap, move);

        // Assert
        valid.ShouldBeFalse();
    }

    [Test]
    public void SwapThatRestoresTheCanon_IsAccepted()
    {
        // Arrange: the canon with day 0 of rows 0 and 1 swapped; swapping them back restores the canon.
        var bitmap = HarmonyTestPlans.Bitmap(Pattern, Shifted(5), Shifted(10));
        Swap(bitmap, 0, 1, 0);
        var guard = HarmonyGuardReplaceValidator.ForStart(new AllowAll(), bitmap);

        // Act
        var valid = guard.IsValid(bitmap, new ReplaceMove(0, 1, 0));

        // Assert
        valid.ShouldBeTrue();
    }

    [Test]
    public void SameSymbolSwap_IsAcceptedWithoutChangingThePlan()
    {
        var bitmap = HarmonyTestPlans.Bitmap(Pattern, Pattern);
        var guard = HarmonyGuardReplaceValidator.ForStart(new AllowAll(), bitmap);

        guard.IsValid(bitmap, new ReplaceMove(0, 1, 0)).ShouldBeTrue();
    }

    [Test]
    public void RuleVeto_StaysAVeto()
    {
        var bitmap = HarmonyTestPlans.Bitmap(Pattern, Shifted(5));
        var guard = HarmonyGuardReplaceValidator.ForStart(new DenyAll(), bitmap);

        guard.IsValid(bitmap, new ReplaceMove(0, 1, 0)).ShouldBeFalse();
    }

    [Test]
    public void Check_LeavesThePlanUnchanged()
    {
        var bitmap = HarmonyTestPlans.Bitmap(Pattern, Shifted(5), Shifted(10));
        var before = Enumerable.Range(0, bitmap.RowCount).Select(r => HarmonyTestPlans.Row(bitmap, r)).ToList();
        var guard = HarmonyGuardReplaceValidator.ForStart(new AllowAll(), bitmap);

        for (var day = 0; day < bitmap.DayCount; day++)
        {
            guard.IsValid(bitmap, new ReplaceMove(0, 1, day));
        }

        Enumerable.Range(0, bitmap.RowCount).Select(r => HarmonyTestPlans.Row(bitmap, r)).ShouldBe(before);
    }

    private static void Swap(HarmonyBitmap bitmap, int rowA, int rowB, int day)
    {
        var a = bitmap.GetCell(rowA, day);
        bitmap.SetCell(rowA, day, bitmap.GetCell(rowB, day));
        bitmap.SetCell(rowB, day, a);
    }
}
