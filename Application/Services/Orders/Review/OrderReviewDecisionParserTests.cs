// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderReviewDecisionParser: start, expenses with comma or dot amounts and the taxable marker, the
/// "this"/"all" decline, and refusals for an unreadable start, an unreadable or non-positive expense and an unknown
/// decline value.
/// </summary>

using Klacks.Api.Application.Services.Orders.Review;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderReviewDecisionParserTests
{
    [Test]
    public void ReadsTargetStartExpensesAndFlags()
    {
        var parameters = new Dictionary<string, object>
        {
            ["target"] = "B1",
            ["startTime"] = "07:00",
            ["split"] = "3x8",
            ["expenses"] = "Mahlzeit:15,50; Fahrt:8.20:taxable",
            ["acceptProposals"] = true,
        };

        var (decision, error) = OrderReviewDecisionParser.Parse(parameters, "de");

        Assert.That(error, Is.Null);
        Assert.That(decision!.Target, Is.EqualTo("B1"));
        Assert.That(decision.StartTime, Is.EqualTo(new TimeOnly(7, 0)));
        Assert.That(decision.Split, Is.EqualTo("3x8"));
        Assert.That(decision.AcceptProposals, Is.True);
        Assert.That(decision.Expenses!.Select(e => (e.Description, e.Amount, e.Taxable)), Is.EqualTo(new[]
        {
            ("Mahlzeit", 15.50m, false),
            ("Fahrt", 8.20m, true),
        }));
    }

    [TestCase("this", OrderReviewExpenseStatus.DeclinedOnce)]
    [TestCase("ALL", OrderReviewExpenseStatus.DeclinedFinal)]
    public void ReadsTheExpenseDecline(string raw, OrderReviewExpenseStatus expected)
    {
        var (decision, _) = OrderReviewDecisionParser.Parse(
            new Dictionary<string, object> { ["target"] = "S1", ["noExpenses"] = raw }, "en");

        Assert.That(decision!.ExpenseDecline, Is.EqualTo(expected));
    }

    [TestCase("startTime", "seven-ish")]
    [TestCase("expenses", "Mahlzeit")]
    [TestCase("expenses", "Mahlzeit:0")]
    [TestCase("noExpenses", "maybe")]
    public void RefusesAnUnreadableValue(string name, string value)
    {
        var (decision, error) = OrderReviewDecisionParser.Parse(
            new Dictionary<string, object> { ["target"] = "B1", [name] = value }, "en");

        Assert.That(decision, Is.Null);
        Assert.That(error, Is.Not.Null.And.Not.Empty);
    }
}
