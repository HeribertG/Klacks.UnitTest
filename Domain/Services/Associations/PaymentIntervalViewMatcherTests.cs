// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one view-interval rule: Monthly and MonthlyTargetHours views see both calendar-month contract kinds (Weg 1),
/// every other interval matches itself only, and an unknown view interval matches nothing.
/// </summary>

using Klacks.Api.Domain.Services.Associations;

namespace Klacks.UnitTest.Domain.Services.Associations;

[TestFixture]
public class PaymentIntervalViewMatcherTests
{
    [TestCase(PaymentInterval.Monthly, PaymentInterval.Monthly, true)]
    [TestCase(PaymentInterval.MonthlyTargetHours, PaymentInterval.Monthly, true)]
    [TestCase(PaymentInterval.Monthly, PaymentInterval.MonthlyTargetHours, true)]
    [TestCase(PaymentInterval.MonthlyTargetHours, PaymentInterval.MonthlyTargetHours, true)]
    [TestCase(PaymentInterval.Weekly, PaymentInterval.Monthly, false)]
    [TestCase(PaymentInterval.Biweekly, PaymentInterval.MonthlyTargetHours, false)]
    [TestCase(PaymentInterval.Individual, PaymentInterval.Monthly, false)]
    [TestCase(PaymentInterval.Weekly, PaymentInterval.Weekly, true)]
    [TestCase(PaymentInterval.Monthly, PaymentInterval.Weekly, false)]
    [TestCase(PaymentInterval.Individual, PaymentInterval.Individual, true)]
    public void Matches_CalendarMonthKindsSeeEachOther_OthersMatchThemselvesOnly(
        PaymentInterval contractInterval, PaymentInterval viewInterval, bool expected)
    {
        PaymentIntervalViewMatcher.Matches(contractInterval, (int)viewInterval).ShouldBe(expected);
    }

    [Test]
    public void AcceptedContractIntervals_MonthlyView_IsMonthlyAndMonthlyTargetHours()
    {
        PaymentIntervalViewMatcher.AcceptedContractIntervals((int)PaymentInterval.Monthly)
            .ShouldBe([PaymentInterval.Monthly, PaymentInterval.MonthlyTargetHours], ignoreOrder: true);
    }

    [Test]
    public void AcceptedContractIntervals_UnknownView_MatchesNothing()
    {
        PaymentIntervalViewMatcher.AcceptedContractIntervals(99).ShouldBeEmpty();
    }
}
