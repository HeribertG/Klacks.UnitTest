// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for FullDayOrderRule: the equal split from a start tiles the day, a part over midnight counts 8 hours, and
/// the edges of the order period follow the owner decision of 2026-10-23 - start 07:00 split 3x8 leaves the first day
/// uncovered until 07:00 and runs the last service to 07:00 of the following day, start 08:00 split 3x8 ends the last
/// day at midnight, start at midnight covers whole days.
/// </summary>

using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Assistant.OrderReview;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class FullDayOrderRuleTests
{
    private static readonly TimeOnly Seven = new(7, 0);
    private static readonly TimeOnly Eight = new(8, 0);

    [Test]
    public void EqualParts_ThreeFromSeven_TileTheDayOverMidnight()
    {
        var parts = FullDayOrderRule.EqualParts(Seven, 3);

        Assert.That(parts.Select(p => (p.Start, p.End)), Is.EqualTo(new[]
        {
            (new TimeOnly(7, 0), new TimeOnly(15, 0)),
            (new TimeOnly(15, 0), new TimeOnly(23, 0)),
            (new TimeOnly(23, 0), new TimeOnly(7, 0)),
        }));
        Assert.That(parts.Select(FullDayOrderRule.PartHours), Is.All.EqualTo(8m));
    }

    [Test]
    public void EqualParts_TwoFromSeven_AreTwelveHoursEach()
    {
        var parts = FullDayOrderRule.EqualParts(Seven, 2);

        Assert.That(parts.Select(FullDayOrderRule.PartHours), Is.All.EqualTo(12m));
        Assert.That(parts[1].End, Is.EqualTo(Seven));
    }

    [Test]
    public void Coverage_StartSevenSplitThreeByEight_FirstDayUncoveredUntilSeven_LastServiceUntilSevenNextDay()
    {
        var coverage = FullDayOrderRule.Coverage(Seven, FullDayOrderRule.EqualParts(Seven, 3));

        Assert.That(coverage.FirstDayUncoveredUntil, Is.EqualTo(Seven));
        Assert.That(coverage.LastDayEndsNextDayAt, Is.EqualTo(Seven));
    }

    [Test]
    public void Coverage_StartEightSplitThreeByEight_LastDayEndsAtMidnight()
    {
        var coverage = FullDayOrderRule.Coverage(Eight, FullDayOrderRule.EqualParts(Eight, 3));

        Assert.That(coverage.LastDayEndsNextDayAt, Is.Null);
        Assert.That(coverage.FirstDayUncoveredUntil, Is.Null);
    }

    [Test]
    public void Coverage_StartAtMidnight_CoversWholeDays()
    {
        var split = FullDayOrderRule.Coverage(TimeOnly.MinValue, FullDayOrderRule.EqualParts(TimeOnly.MinValue, 3));
        var unsplit = FullDayOrderRule.Coverage(TimeOnly.MinValue, []);

        Assert.That(split, Is.EqualTo(unsplit));
        Assert.That(unsplit.FirstDayUncoveredUntil, Is.Null);
        Assert.That(unsplit.LastDayEndsNextDayAt, Is.Null);
    }

    [Test]
    public void Coverage_UnsplitFromSeven_RunsEveryServiceToSevenNextDay()
    {
        var coverage = FullDayOrderRule.Coverage(Seven, Array.Empty<OrderReviewPart>());

        Assert.That(coverage.FirstDayUncoveredUntil, Is.EqualTo(Seven));
        Assert.That(coverage.LastDayEndsNextDayAt, Is.EqualTo(Seven));
    }

    [Test]
    public void IsFullDay_OnlyWhenStartEqualsEnd()
    {
        Assert.That(FullDayOrderRule.IsFullDay(new Shift { StartShift = Seven, EndShift = Seven }), Is.True);
        Assert.That(FullDayOrderRule.IsFullDay(new Shift { StartShift = Seven, EndShift = Eight }), Is.False);
    }
}
