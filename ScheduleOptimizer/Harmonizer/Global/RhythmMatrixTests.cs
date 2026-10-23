// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;

/// <summary>
/// The region rebuild keeps the rhythm matrix in step with a changing plan: refreshing the changed rows must give the
/// same value as building the matrix again, and a clone must not see changes made to the original.
/// </summary>
[TestFixture]
public sealed class RhythmMatrixTests
{
    [Test]
    public void Refresh_EqualsAFullRebuild()
    {
        var bitmap = HarmonyTestPlans.Bitmap("EEE__LLL__NNN__", "__EEE__LLL__NNN", "NNN__EEE__LLL__");
        var matrix = new RhythmMatrix(bitmap);
        var swapped = bitmap.GetCell(0, 0);
        bitmap.SetCell(0, 0, bitmap.GetCell(1, 0));
        bitmap.SetCell(1, 0, swapped);

        matrix.Refresh(bitmap, [0, 1]);

        matrix.Rhythm.ShouldBe(new RhythmMatrix(bitmap).Rhythm, 1e-12);
    }

    [Test]
    public void Clone_IsIndependent()
    {
        var bitmap = HarmonyTestPlans.Bitmap("EEE__LLL__NNN__", "__EEE__LLL__NNN");
        var matrix = new RhythmMatrix(bitmap);
        var clone = matrix.Clone();
        bitmap.SetCell(0, 0, Cell.Free());
        bitmap.SetCell(0, 1, Cell.Free());

        matrix.Refresh(bitmap, [0]);

        clone.Rhythm.ShouldBe(1d, 1e-12);
        matrix.Rhythm.ShouldBeLessThan(1d);
    }

    [Test]
    public void RawRows_GiveTheSameRhythmAsTheBitmap()
    {
        var bitmap = HarmonyTestPlans.Bitmap("EEE__LLL__NNN__", "_EEE_LLL___NNN_");
        var rows = Enumerable.Range(0, bitmap.RowCount)
            .Select(r => Enumerable.Range(0, bitmap.DayCount).Select(d => (byte)bitmap.GetCell(r, d).Symbol).ToArray())
            .ToList();

        new RhythmMatrix(rows).Rhythm.ShouldBe(new RhythmMatrix(bitmap).Rhythm, 1e-12);
    }
}
