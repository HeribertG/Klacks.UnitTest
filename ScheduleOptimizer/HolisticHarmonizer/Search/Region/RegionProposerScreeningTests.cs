// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Global;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search.Region;
using Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Global;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search.Region;

/// <summary>
/// Coarse-to-fine region choice: the screened regions come on top of the plain ones (the plain proposer is kept
/// exactly), and the coarse score counts how many cells could be brought onto the canon by rearranging a day's cells
/// among the region rows.
/// </summary>
[TestFixture]
public sealed class RegionProposerScreeningTests
{
    private static readonly string[] Canon = ["EELL__", "LL__EE", "__EELL", "EELL__"];

    // Day 0 of rows 0 and 1 swapped: both rows now miss the canon on that day.
    private static readonly string[] Swapped = ["LELL__", "EL__EE", "__EELL", "EELL__"];

    [Test]
    public void CoarseGain_is_zero_where_every_cell_already_sits_on_the_canon()
    {
        // Arrange
        var bitmap = HarmonyTestPlans.Bitmap(Canon);
        var expected = ExpectedRows(bitmap);

        // Act
        var gain = RegionProposer.CoarseGain(bitmap, expected, new HarmonyRegion([0, 1, 2, 3], 0, 5));

        // Assert
        gain.ShouldBe(0);
    }

    [Test]
    public void CoarseGain_counts_the_cells_a_rearrangement_could_bring_back_onto_the_canon()
    {
        // Arrange
        var bitmap = HarmonyTestPlans.Bitmap(Swapped);
        var expected = ExpectedRows(bitmap);

        // Act
        var inside = RegionProposer.CoarseGain(bitmap, expected, new HarmonyRegion([0, 1], 0, 0));
        var outside = RegionProposer.CoarseGain(bitmap, expected, new HarmonyRegion([2, 3], 0, 5));

        // Assert
        inside.ShouldBe(2);
        outside.ShouldBe(0);
    }

    [Test]
    public void Screened_regions_come_on_top_of_the_unchanged_plain_regions()
    {
        // Arrange
        var bitmap = HarmonyTestPlans.Bitmap(Swapped);
        var rhythm = new RhythmMatrix(bitmap);
        var plainOptions = RegionRebuildOptions.Default with { ScreenedRegions = 0, RowsPerRegion = 2, MinDays = 1, MaxDays = 3 };
        var screenedOptions = plainOptions with { ScreenedRegions = RegionRebuildOptions.DefaultScreenedRegions };

        // Act
        var plain = RegionProposer.Propose(bitmap, rhythm, plainOptions, new Random(7));
        var screened = RegionProposer.Propose(bitmap, rhythm, screenedOptions, new Random(7));

        // Assert
        plain.Count.ShouldBe(plainOptions.RegionsPerRound);
        Describe(screened.Take(plain.Count)).ShouldBe(Describe(plain));
        screened.Count.ShouldBeGreaterThan(plain.Count);
        screened.Skip(plain.Count).ShouldAllBe(r => RegionProposer.CoarseGain(bitmap, ExpectedRows(bitmap), r) > 0);
    }

    private static byte[]?[] ExpectedRows(HarmonyBitmap bitmap)
    {
        var rhythm = new RhythmMatrix(bitmap);
        return Enumerable.Range(0, bitmap.RowCount).Select(rhythm.ExpectedRow).ToArray();
    }

    private static IEnumerable<string> Describe(IEnumerable<HarmonyRegion> regions)
        => regions.Select(r => $"{string.Join('-', r.Rows)}:{r.DayFrom}..{r.DayTo}").ToList();
}
