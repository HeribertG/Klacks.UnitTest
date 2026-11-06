// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// The end-state check of a rebuilt row also guards the weekly rest days. A cell checked in place cannot see them
/// (there is no plan before the change to compare with), so the row check compares the rebuilt row with the original
/// one: a rebuild that takes a calendar week below the agent's minimum rest days is rejected.
/// </summary>
[TestFixture]
public class DomainAwareReplaceValidatorRowRebuildRestDayTests
{
    private const int MinRestDays = 2;
    private const int ReceivingRow = 0;
    private const int GivingRow = 1;
    private const int Saturday = 5;
    private const string RestDayReasonFragment = "MinRestDays";

    [Test]
    public void DiagnoseRowRebuild_ReceivingAWorkDayThatLeavesTooFewRestDays_Rejected()
    {
        var (original, rebuilt, validator) = SwapSaturday("EEEEE__", "_____E_");

        validator.DiagnoseCellInPlace(rebuilt, ReceivingRow, Saturday).ShouldBeNull();
        var reason = validator.DiagnoseRowRebuild(original, rebuilt, ReceivingRow, [Saturday]);

        reason.ShouldNotBeNull();
        reason.ShouldContain(RestDayReasonFragment);
    }

    [Test]
    public void DiagnoseRowRebuild_ReceivingAWorkDayThatKeepsEnoughRestDays_Allowed()
    {
        var (original, rebuilt, validator) = SwapSaturday("EEEE___", "_____E_");

        validator.DiagnoseRowRebuild(original, rebuilt, ReceivingRow, [Saturday]).ShouldBeNull();
        validator.DiagnoseRowRebuild(original, rebuilt, GivingRow, [Saturday]).ShouldBeNull();
    }

    private static (HarmonyBitmap Original, HarmonyBitmap Rebuilt, DomainAwareReplaceValidator Validator) SwapSaturday(
        params string[] rows)
    {
        var input = HarmonyTestPlans.Input(rows);
        input = input with { Agents = input.Agents.Select(a => a with { MinRestDays = MinRestDays }).ToList() };
        var original = BitmapBuilder.Build(input);
        var rebuilt = BitmapCloner.Clone(original);
        rebuilt.SetCell(ReceivingRow, Saturday, original.GetCell(GivingRow, Saturday));
        rebuilt.SetCell(GivingRow, Saturday, original.GetCell(ReceivingRow, Saturday));
        return (original, rebuilt, DomainAwareReplaceValidator.ForInput(input, null));
    }
}
