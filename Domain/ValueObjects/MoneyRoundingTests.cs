// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins Money: amounts are rounded to two decimal places (banker's rounding) when the value object is built, and
/// FromCents converts with a factor of 100. A refactoring that names the decimal places and the cents factor must
/// keep both.
/// </summary>

using Klacks.Api.Domain.ValueObjects;

namespace Klacks.UnitTest.Domain.ValueObjects;

[TestFixture]
public class MoneyRoundingTests
{
    [TestCase(1.234, 1.23)]
    [TestCase(1.236, 1.24)]
    [TestCase(2.345, 2.34)]
    [TestCase(2.355, 2.36)]
    public void Create_RoundsToTwoDecimalPlaces(double given, double expected)
    {
        var money = Money.Create((decimal)given, "CHF");

        money.Amount.ShouldBe((decimal)expected);
    }

    [TestCase(1999, 19.99)]
    [TestCase(5, 0.05)]
    [TestCase(100, 1.00)]
    [TestCase(-250, -2.50)]
    public void FromCents_DividesByOneHundred(int cents, double expected)
    {
        var money = Money.FromCents(cents, "CHF");

        money.Amount.ShouldBe((decimal)expected);
    }

    [Test]
    public void Arithmetic_RoundsTheResultToTwoDecimalPlaces()
    {
        var money = Money.Create(10m, "CHF").Multiply(0.3333m);

        money.Amount.ShouldBe(3.33m);
    }
}
