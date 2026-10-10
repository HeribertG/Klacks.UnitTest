// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ContractWorkdays and ContractPaymentIntervalParser: strict, case-insensitive token parsing,
/// the rejection of anything that is not exactly a weekday token or an interval name (including the
/// Enum.TryParse comma and number traps), formatting in calendar order and writing the seven weekday flags.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Services.Contracts;

namespace Klacks.UnitTest.Application.Services.Contracts;

[TestFixture]
public class ContractWorkdaysTests
{
    [Test]
    public void TryParse_CaseInsensitiveTokensWithSpaces_ReturnsTheDaySet()
    {
        var ok = ContractWorkdays.TryParse(" mon, TUE ,Wed,thu,Fri", out var days, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeNull();
        days.ShouldBe(
            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
            ignoreOrder: true);
    }

    [TestCase("Monday")]
    [TestCase("Mon,Foo")]
    [TestCase("Mon,,Tue")]
    [TestCase("Mon Tue")]
    [TestCase("Mo")]
    [TestCase(",")]
    [TestCase("1,2")]
    public void TryParse_UnknownOrMalformedToken_IsRejectedWithAnErrorListingTheAllowedTokens(string raw)
    {
        var ok = ContractWorkdays.TryParse(raw, out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("Mon,Tue,Wed,Thu,Fri,Sat,Sun");
    }

    [Test]
    public void Format_ListsTokensInCalendarOrder_RegardlessOfSetOrder()
    {
        var days = new HashSet<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Saturday };

        ContractWorkdays.Format(days).ShouldBe("Mon,Sat,Sun");
    }

    [Test]
    public void Apply_SetsListedDaysTrue_AndAllOthersFalse()
    {
        var resource = new ContractResource();

        ContractWorkdays.Apply(resource, new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday });

        resource.WorkOnMonday.ShouldBeFalse();
        resource.WorkOnTuesday.ShouldBeFalse();
        resource.WorkOnWednesday.ShouldBeFalse();
        resource.WorkOnThursday.ShouldBeFalse();
        resource.WorkOnFriday.ShouldBeFalse();
        resource.WorkOnSaturday.ShouldBeTrue();
        resource.WorkOnSunday.ShouldBeTrue();
        ContractWorkdays.Of(resource).Count.ShouldBe(2);
    }

    [Test]
    public void Of_Contract_ReadsTheSevenFlags()
    {
        var contract = new Contract { WorkOnWednesday = false, WorkOnSaturday = true };

        ContractWorkdays.Format(ContractWorkdays.Of(contract)).ShouldBe("Mon,Tue,Thu,Fri,Sat");
    }

    [TestCase("Weekly", PaymentInterval.Weekly)]
    [TestCase("biweekly", PaymentInterval.Biweekly)]
    [TestCase(" MONTHLY ", PaymentInterval.Monthly)]
    [TestCase("individual", PaymentInterval.Individual)]
    [TestCase("MonthlyTargetHours", PaymentInterval.MonthlyTargetHours)]
    public void IntervalParser_AcceptsEveryMemberName_CaseInsensitive(string raw, PaymentInterval expected)
    {
        ContractPaymentIntervalParser.TryParse(raw, out var interval, out var error).ShouldBeTrue();

        interval.ShouldBe(expected);
        error.ShouldBeNull();
    }

    [TestCase("Weekly,Monthly")]
    [TestCase("2")]
    [TestCase("99")]
    [TestCase("")]
    [TestCase("Quarterly")]
    public void IntervalParser_RejectsListsNumbersAndUnknownNames(string raw)
    {
        ContractPaymentIntervalParser.TryParse(raw, out _, out var error).ShouldBeFalse();

        error.ShouldNotBeNull();
        error.ShouldContain("MonthlyTargetHours");
    }
}
