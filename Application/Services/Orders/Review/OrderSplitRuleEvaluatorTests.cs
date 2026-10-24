// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OrderSplitRuleEvaluator: a part must fit the maximum daily hours, the daily work frame and leave the
/// minimum rest in the rest of the day, judged per member; the verdict says all, some (k of n), none, or no members.
/// </summary>

using Klacks.Api.Application.Services.Orders.Review;
using Klacks.Api.Domain.Models.Scheduling;

namespace Klacks.UnitTest.Application.Services.Orders.Review;

[TestFixture]
public class OrderSplitRuleEvaluatorTests
{
    private static SchedulingPolicy Policy(double maxDailyHours = 10, double minRestHours = 11, double? maxDailySpanHours = null) =>
        new(
            TimeSpan.FromHours(minRestHours),
            TimeSpan.FromHours(maxDailyHours),
            6,
            TimeSpan.FromHours(50),
            2m,
            maxDailySpanHours.HasValue ? TimeSpan.FromHours(maxDailySpanHours.Value) : null);

    [Test]
    public void EightHourParts_UnderDefaultRules_AreAllowedForEveryMember()
    {
        var verdict = OrderSplitRuleEvaluator.Evaluate(8m, [Policy(), Policy()]);

        Assert.That(verdict.Kind, Is.EqualTo(OrderSplitVerdictKind.Allowed));
        Assert.That((verdict.AllowedMembers, verdict.MemberCount), Is.EqualTo((2, 2)));
    }

    [Test]
    public void TwelveHourParts_OverTheDailyMaximum_AreNotAllowed()
    {
        var verdict = OrderSplitRuleEvaluator.Evaluate(12m, [Policy(), Policy()]);

        Assert.That(verdict.Kind, Is.EqualTo(OrderSplitVerdictKind.NotAllowed));
    }

    [Test]
    public void TwelveHourParts_AllowedByOnlyOneContract_AreAllowedForSome()
    {
        var verdict = OrderSplitRuleEvaluator.Evaluate(12m, [Policy(maxDailyHours: 12), Policy()]);

        Assert.That(verdict.Kind, Is.EqualTo(OrderSplitVerdictKind.AllowedForSome));
        Assert.That((verdict.AllowedMembers, verdict.MemberCount), Is.EqualTo((1, 2)));
    }

    [Test]
    public void APart_ThatLeavesTooLittleRest_IsNotAllowed()
    {
        var verdict = OrderSplitRuleEvaluator.Evaluate(8m, [Policy(minRestHours: 17)]);

        Assert.That(verdict.Kind, Is.EqualTo(OrderSplitVerdictKind.NotAllowed));
    }

    [Test]
    public void APart_LongerThanTheDailyWorkFrame_IsNotAllowed()
    {
        var verdict = OrderSplitRuleEvaluator.Evaluate(8m, [Policy(maxDailyHours: 12, maxDailySpanHours: 7)]);

        Assert.That(verdict.Kind, Is.EqualTo(OrderSplitVerdictKind.NotAllowed));
    }

    [Test]
    public void NoMembers_GiveNoVerdict()
    {
        var verdict = OrderSplitRuleEvaluator.Evaluate(8m, []);

        Assert.That(verdict.Kind, Is.EqualTo(OrderSplitVerdictKind.NoMembers));
    }
}
