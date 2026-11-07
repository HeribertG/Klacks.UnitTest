using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodBoundariesTests
{
    private static Group MakeGroup(PaymentInterval interval, DateTime? validFrom = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Bern",
        PaymentInterval = interval,
        ValidFrom = validFrom ?? new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public void Monthly_StartsOnFirstOfNextMonth_EndsOnLastDay()
    {
        var group = MakeGroup(PaymentInterval.Monthly);
        var today = new DateOnly(2026, 1, 28);

        var start = NextPeriodBoundaries.ComputeStart(group, today, new DateOnly(2026, 2, 2));
        var end = NextPeriodBoundaries.ComputeEnd(group, start);

        Assert.That(start, Is.EqualTo(new DateOnly(2026, 2, 1)));
        Assert.That(end, Is.EqualTo(new DateOnly(2026, 2, 28)));
    }

    [Test]
    public void Monthly_InDecember_RollsIntoNextYear()
    {
        var group = MakeGroup(PaymentInterval.MonthlyTargetHours);

        var start = NextPeriodBoundaries.ComputeStart(group, new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 21));

        Assert.That(start, Is.EqualTo(new DateOnly(2027, 1, 1)));
    }

    [Test]
    public void Weekly_UsesSuppliedNextWeekStart_AndSevenDayPeriod()
    {
        var group = MakeGroup(PaymentInterval.Weekly);
        var nextWeekStart = new DateOnly(2026, 2, 2);

        var start = NextPeriodBoundaries.ComputeStart(group, new DateOnly(2026, 1, 28), nextWeekStart);
        var end = NextPeriodBoundaries.ComputeEnd(group, start);

        Assert.That(start, Is.EqualTo(nextWeekStart));
        Assert.That(end, Is.EqualTo(new DateOnly(2026, 2, 8)));
    }

    [Test]
    public void Biweekly_FollowsTheFixedPayPeriodAnchor_NotTheGroupValidFrom()
    {
        var group = MakeGroup(PaymentInterval.Biweekly, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        var start = NextPeriodBoundaries.ComputeStart(group, new DateOnly(2026, 1, 28), new DateOnly(2026, 2, 2));
        var end = NextPeriodBoundaries.ComputeEnd(group, start);

        Assert.That(start, Is.EqualTo(new DateOnly(2026, 2, 9)));
        Assert.That(end, Is.EqualTo(new DateOnly(2026, 2, 22)));
    }

    [Test]
    public void Biweekly_AcrossTheIsoYearWith53Weeks_TheNextPeriodStartsOn2027_01_11()
    {
        var group = MakeGroup(PaymentInterval.Biweekly);

        var start = NextPeriodBoundaries.ComputeStart(group, new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 11));

        Assert.That(start, Is.EqualTo(new DateOnly(2027, 1, 11)));
        Assert.That(NextPeriodBoundaries.ComputeEnd(group, start), Is.EqualTo(new DateOnly(2027, 1, 24)));
    }

    [Test]
    public void Biweekly_WithSundayWeekStart_FollowsTheAnchorMovedToSunday()
    {
        var group = MakeGroup(PaymentInterval.Biweekly);

        var start = NextPeriodBoundaries.ComputeStart(group, new DateOnly(2027, 1, 4), new DateOnly(2027, 1, 10));

        Assert.That(start, Is.EqualTo(new DateOnly(2027, 1, 10)));
        Assert.That(NextPeriodBoundaries.ComputeEnd(group, start), Is.EqualTo(new DateOnly(2027, 1, 23)));
    }

    [Test]
    public void Individual_HasNoDerivableCycle_AndComputeThrows()
    {
        var group = MakeGroup(PaymentInterval.Individual);

        Assert.That(NextPeriodBoundaries.HasDerivableCycle(group.PaymentInterval), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NextPeriodBoundaries.ComputeStart(group, new DateOnly(2026, 1, 28), new DateOnly(2026, 2, 2)));
    }
}
