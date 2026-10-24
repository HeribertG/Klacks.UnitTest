// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewSplitSpec: 3x8 and 2x12 split a full-day order from its chosen start, "none" keeps it
/// whole, explicit parts must tile the order and get their names by position, an equal split of an order that is not
/// full-day is refused.
/// </summary>

using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Assistant.OrderReview;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderReviewSplitSpecTests
{
    private static OrderReviewDraftItem FullDay(TimeOnly? chosenStart = null) => new()
    {
        OrderName = "Wache",
        IsFullDay = true,
        CurrentStart = TimeOnly.MinValue,
        CurrentEnd = TimeOnly.MinValue,
        ProposedStart = new TimeOnly(7, 0),
        ChosenStart = chosenStart,
    };

    [Test]
    public void ThreeByEight_StartsAtTheChosenStart()
    {
        var (parts, error) = OrderReviewSplitSpec.Resolve(FullDay(new TimeOnly(8, 0)), "3x8", "Früh,Spät,Nacht", "de");

        Assert.That(error, Is.Null);
        Assert.That(parts!.Select(p => (p.Start, p.Name)), Is.EqualTo(new[]
        {
            (new TimeOnly(8, 0), "Früh"),
            (new TimeOnly(16, 0), "Spät"),
            (TimeOnly.MinValue, "Nacht"),
        }));
    }

    [Test]
    public void TwoByTwelve_FromTheProposedStart()
    {
        var (parts, _) = OrderReviewSplitSpec.Resolve(FullDay(), "2X12", null, "en");

        Assert.That(parts!.Select(p => p.Start), Is.EqualTo(new[] { new TimeOnly(7, 0), new TimeOnly(19, 0) }));
    }

    [Test]
    public void None_KeepsTheOrderWhole()
    {
        var (parts, error) = OrderReviewSplitSpec.Resolve(FullDay(), "none", null, "en");

        Assert.That(error, Is.Null);
        Assert.That(parts, Is.Empty);
    }

    [Test]
    public void ExplicitParts_MustTileTheOrder()
    {
        var item = FullDay(new TimeOnly(7, 0));

        var (valid, validError) = OrderReviewSplitSpec.Resolve(item, "07:00-19:00,19:00-07:00", null, "de");
        var (gap, gapError) = OrderReviewSplitSpec.Resolve(item, "07:00-15:00,16:00-07:00", null, "de");

        Assert.That(validError, Is.Null);
        Assert.That(valid, Has.Count.EqualTo(2));
        Assert.That(gap, Is.Null);
        Assert.That(gapError, Does.Contain("gap"));
    }

    [Test]
    public void AnEqualSplit_OfAnOrderThatIsNotFullDay_IsRefused()
    {
        var item = new OrderReviewDraftItem
        {
            OrderName = "Früh",
            CurrentStart = new TimeOnly(6, 0),
            CurrentEnd = new TimeOnly(14, 0),
            ChosenStart = new TimeOnly(6, 0),
        };

        var (parts, error) = OrderReviewSplitSpec.Resolve(item, "3x8", null, "de");

        Assert.That(parts, Is.Null);
        Assert.That(error, Does.Contain("full-day"));
    }
}
