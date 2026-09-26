using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseDateCalculatorTests
{
    [TestCase(0, true)]
    [TestCase(1, true)]
    [TestCase(31, true)]
    [TestCase(-1, false)]
    [TestCase(32, false)]
    [TestCase(int.MinValue, false)]
    [TestCase(int.MaxValue, false)]
    public void IsValidLag_AcceptsExactlyTheClosedRangeZeroToThirtyOne(int lagDays, bool expected)
    {
        Assert.That(PeriodCloseDateCalculator.IsValidLag(lagDays), Is.EqualTo(expected));
    }

    [Test]
    public void Bounds_AreZeroAndThirtyOne()
    {
        Assert.That(PeriodCloseDateCalculator.MinLagDays, Is.EqualTo(0));
        Assert.That(PeriodCloseDateCalculator.MaxLagDays, Is.EqualTo(31));
    }

    [Test]
    public void CloseDateFor_LagZero_IsThePeriodEnd()
    {
        var end = new DateOnly(2026, 1, 31);

        Assert.That(PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Monthly, end, 0), Is.EqualTo(end));
    }

    [Test]
    public void CloseDateFor_AddsTheLagAcrossTheMonthEnd()
    {
        var close = PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Monthly, new DateOnly(2026, 1, 31), 5);

        Assert.That(close, Is.EqualTo(new DateOnly(2026, 2, 5)));
    }

    [Test]
    public void CloseDateFor_MaximumLagAcrossTheYearEnd()
    {
        var close = PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Weekly, new DateOnly(2026, 12, 31), 31);

        Assert.That(close, Is.EqualTo(new DateOnly(2027, 1, 31)));
    }

    [Test]
    public void CloseDateFor_LeapYearFebruary_CountsTheExtraDay()
    {
        var leap = PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Monthly, new DateOnly(2028, 2, 29), 1);
        var regular = PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Monthly, new DateOnly(2027, 2, 28), 1);

        Assert.That(leap, Is.EqualTo(new DateOnly(2028, 3, 1)));
        Assert.That(regular, Is.EqualTo(new DateOnly(2027, 3, 1)));
    }

    [TestCase(PaymentInterval.Weekly)]
    [TestCase(PaymentInterval.Biweekly)]
    [TestCase(PaymentInterval.Monthly)]
    [TestCase(PaymentInterval.MonthlyTargetHours)]
    public void CloseDateFor_EveryDerivableInterval_HasADate(PaymentInterval interval)
    {
        Assert.That(PeriodCloseDateCalculator.CloseDateFor(interval, new DateOnly(2026, 1, 31), 3), Is.Not.Null);
    }

    [Test]
    public void CloseDateFor_Individual_HasNoDate()
    {
        Assert.That(PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Individual, new DateOnly(2026, 1, 31), 3), Is.Null);
    }

    [TestCase(-1)]
    [TestCase(32)]
    public void CloseDateFor_InvalidLag_HasNoDateInsteadOfADateBeforeThePeriodEnd(int lagDays)
    {
        Assert.That(PeriodCloseDateCalculator.CloseDateFor(PaymentInterval.Monthly, new DateOnly(2026, 1, 31), lagDays), Is.Null);
    }

    [Test]
    public void IsAutoCloseDue_LagZero_NotOnThePeriodEndButFromTheNextDay()
    {
        var end = new DateOnly(2026, 1, 31);

        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(end, end, 0), Is.False);
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(end.AddDays(1), end, 0), Is.True);
    }

    [Test]
    public void IsAutoCloseDue_WithLag_OnlyFromTheCloseDate()
    {
        var end = new DateOnly(2026, 1, 31);

        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2026, 2, 4), end, 5), Is.False);
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2026, 2, 5), end, 5), Is.True);
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2026, 2, 6), end, 5), Is.True);
    }

    [Test]
    public void IsAutoCloseDue_BeforeThePeriodEnd_IsNeverDue()
    {
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 31), 0), Is.False);
    }

    [TestCase(-1)]
    [TestCase(32)]
    public void IsAutoCloseDue_InvalidLag_IsNeverDue(int lagDays)
    {
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2027, 1, 1), new DateOnly(2026, 1, 31), lagDays), Is.False);
    }

    [Test]
    public void IsAutoCloseDue_MaximumLag_DueOnTheThirtyFirstDayAfterTheEnd()
    {
        var end = new DateOnly(2026, 1, 31);

        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2026, 3, 3), end, 31), Is.True);
        Assert.That(PeriodCloseDateCalculator.IsAutoCloseDue(new DateOnly(2026, 3, 2), end, 31), Is.False);
    }
}
