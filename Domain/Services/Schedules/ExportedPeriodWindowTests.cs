// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ExportedPeriodWindow keeps the periods of a person's latest two distinct export ranges recalculable and puts an
/// exported period behind the window only when it ends before the earliest start of those two ranges. Supplementary
/// exports and revisions of the same range count once.
/// </summary>

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ExportedPeriodWindowTests
{
    private static readonly (DateOnly StartDate, DateOnly EndDate) January = (new(2099, 1, 1), new(2099, 1, 31));
    private static readonly (DateOnly StartDate, DateOnly EndDate) February = (new(2099, 2, 1), new(2099, 2, 28));
    private static readonly (DateOnly StartDate, DateOnly EndDate) March = (new(2099, 3, 1), new(2099, 3, 31));
    private static readonly (DateOnly StartDate, DateOnly EndDate) April = (new(2099, 4, 1), new(2099, 4, 30));

    [Test]
    public void TwoDistinctExportRanges_PutNothingBehindTheWindow()
    {
        ExportedPeriodWindow.IsBehindWindow([January, February], January.StartDate, January.EndDate).ShouldBeFalse();
    }

    [Test]
    public void ThreeDistinctExportRanges_PutTheOldestExportedPeriodBehindTheWindow()
    {
        var ranges = new[] { January, February, March };

        ExportedPeriodWindow.IsBehindWindow(ranges, January.StartDate, January.EndDate).ShouldBeTrue();
        ExportedPeriodWindow.IsBehindWindow(ranges, February.StartDate, February.EndDate).ShouldBeFalse();
        ExportedPeriodWindow.IsBehindWindow(ranges, March.StartDate, March.EndDate).ShouldBeFalse();
    }

    [Test]
    public void SupplementaryExportsOfTheSameRange_CountOnce()
    {
        var ranges = new[] { January, February, February, February };

        ExportedPeriodWindow.IsBehindWindow(ranges, January.StartDate, January.EndDate).ShouldBeFalse();
    }

    [Test]
    public void APeriodThatWasNeverExported_IsNotBehindTheWindow()
    {
        var ranges = new[] { February, March, April };

        ExportedPeriodWindow.IsBehindWindow(ranges, January.StartDate, January.EndDate).ShouldBeFalse();
    }

    [Test]
    public void TheLatestRangesAreChosenByEndDate_NotByInputOrder()
    {
        var ranges = new[] { April, January, March, February };

        ExportedPeriodWindow.IsBehindWindow(ranges, February.StartDate, February.EndDate).ShouldBeTrue();
        ExportedPeriodWindow.IsBehindWindow(ranges, March.StartDate, March.EndDate).ShouldBeFalse();
    }

    [Test]
    public void APeriodOverlappingTheEarliestWindowRange_StaysInsideTheWindow()
    {
        var ranges = new[] { January, (new DateOnly(2099, 1, 20), new DateOnly(2099, 2, 28)), March };

        ExportedPeriodWindow.IsBehindWindow(ranges, January.StartDate, January.EndDate).ShouldBeFalse();
    }
}
