// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// Pins the one shift-work rule every planner applies (owner decision 2026-11-05): without shift work only early
/// duties, with shift work every duty. Free and break cells are always permitted; Other is not early.
/// </summary>
[TestFixture]
public class ShiftWorkRuleTests
{
    private const int UnknownShiftTypeIndex = -1;

    [TestCase(true, ShiftTypeInference.EarlyIndex, true)]
    [TestCase(true, ShiftTypeInference.LateIndex, true)]
    [TestCase(true, ShiftTypeInference.NightIndex, true)]
    [TestCase(true, UnknownShiftTypeIndex, true)]
    [TestCase(false, ShiftTypeInference.EarlyIndex, true)]
    [TestCase(false, ShiftTypeInference.LateIndex, false)]
    [TestCase(false, ShiftTypeInference.NightIndex, false)]
    [TestCase(false, UnknownShiftTypeIndex, false)]
    public void Permits_ByShiftTypeIndex(bool performsShiftWork, int shiftTypeIndex, bool expected)
        => ShiftWorkRule.Permits(performsShiftWork, shiftTypeIndex).ShouldBe(expected);

    [TestCase(ShiftTypeInference.EarlyIndex, false)]
    [TestCase(ShiftTypeInference.LateIndex, true)]
    [TestCase(ShiftTypeInference.NightIndex, true)]
    public void RequiresShiftWork_OnlyNonEarly(int shiftTypeIndex, bool expected)
        => ShiftWorkRule.RequiresShiftWork(shiftTypeIndex).ShouldBe(expected);

    [TestCase(CellSymbol.Free, true)]
    [TestCase(CellSymbol.Early, true)]
    [TestCase(CellSymbol.Break, true)]
    [TestCase(CellSymbol.Late, false)]
    [TestCase(CellSymbol.Night, false)]
    [TestCase(CellSymbol.Other, false)]
    public void Permits_BySymbol_WithoutShiftWork(CellSymbol symbol, bool expected)
        => ShiftWorkRule.Permits(false, symbol).ShouldBe(expected);

    [TestCase(CellSymbol.Free)]
    [TestCase(CellSymbol.Early)]
    [TestCase(CellSymbol.Late)]
    [TestCase(CellSymbol.Night)]
    [TestCase(CellSymbol.Other)]
    [TestCase(CellSymbol.Break)]
    public void Permits_BySymbol_WithShiftWork_Always(CellSymbol symbol)
        => ShiftWorkRule.Permits(true, symbol).ShouldBeTrue();

    [Test]
    public void SymbolAndIndexForms_Agree_ForTheThreeRealShiftTypes()
    {
        var pairs = new[]
        {
            (CellSymbol.Early, ShiftTypeInference.EarlyIndex),
            (CellSymbol.Late, ShiftTypeInference.LateIndex),
            (CellSymbol.Night, ShiftTypeInference.NightIndex),
        };

        foreach (var (symbol, index) in pairs)
        {
            foreach (var flag in new[] { true, false })
            {
                ShiftWorkRule.Permits(flag, symbol).ShouldBe(ShiftWorkRule.Permits(flag, index), $"{symbol} / shiftWork={flag}");
            }
        }
    }
}
