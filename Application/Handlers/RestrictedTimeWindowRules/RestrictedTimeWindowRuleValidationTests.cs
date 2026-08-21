// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RestrictedTimeWindowRuleValidation: season month bounds (1-12), season day bounds
/// (1-31) for both endpoints, and that every DailyStart/DailyEnd pair is accepted - per the R1 duration
/// convention equal bounds mean a full 24 hour ban window, not an empty one.
/// </summary>

using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.RestrictedTimeWindowRules;
using Klacks.Api.Domain.Exceptions;

namespace Klacks.UnitTest.Application.Handlers.RestrictedTimeWindowRules;

[TestFixture]
public class RestrictedTimeWindowRuleValidationTests
{
    private static RestrictedTimeWindowRuleResource ValidResource() => new()
    {
        SeasonFromMonth = 6,
        SeasonFromDay = 15,
        SeasonToMonth = 9,
        SeasonToDay = 15,
        DailyStart = new TimeOnly(12, 30),
        DailyEnd = new TimeOnly(15, 0),
        AppliesToGroupTag = "outdoor",
    };

    [Test]
    public void Validate_NullResource_Throws()
    {
        var ex = Should.Throw<InvalidRequestException>(() => RestrictedTimeWindowRuleValidation.Validate(null));
        ex.Message.ShouldBe("Restricted time window rule data is required.");
    }

    [Test]
    public void Validate_ValidResource_DoesNotThrow()
    {
        Should.NotThrow(() => RestrictedTimeWindowRuleValidation.Validate(ValidResource()));
    }

    [Test]
    public void Validate_MonthAndDayBoundaries_DoNotThrow()
    {
        var resource = ValidResource();
        resource.SeasonFromMonth = 1;
        resource.SeasonFromDay = 1;
        resource.SeasonToMonth = 12;
        resource.SeasonToDay = 31;

        Should.NotThrow(() => RestrictedTimeWindowRuleValidation.Validate(resource));
    }

    [TestCase(0)]
    [TestCase(13)]
    public void Validate_InvalidSeasonFromMonth_Throws(int month)
    {
        var resource = ValidResource();
        resource.SeasonFromMonth = month;

        var ex = Should.Throw<InvalidRequestException>(() => RestrictedTimeWindowRuleValidation.Validate(resource));
        ex.Message.ShouldBe("SeasonFromMonth must be between 1 and 12.");
    }

    [TestCase(0)]
    [TestCase(13)]
    public void Validate_InvalidSeasonToMonth_Throws(int month)
    {
        var resource = ValidResource();
        resource.SeasonToMonth = month;

        var ex = Should.Throw<InvalidRequestException>(() => RestrictedTimeWindowRuleValidation.Validate(resource));
        ex.Message.ShouldBe("SeasonToMonth must be between 1 and 12.");
    }

    [TestCase(0)]
    [TestCase(32)]
    public void Validate_InvalidSeasonFromDay_Throws(int day)
    {
        var resource = ValidResource();
        resource.SeasonFromDay = day;

        var ex = Should.Throw<InvalidRequestException>(() => RestrictedTimeWindowRuleValidation.Validate(resource));
        ex.Message.ShouldBe("SeasonFromDay must be between 1 and 31.");
    }

    [TestCase(0)]
    [TestCase(32)]
    public void Validate_InvalidSeasonToDay_Throws(int day)
    {
        var resource = ValidResource();
        resource.SeasonToDay = day;

        var ex = Should.Throw<InvalidRequestException>(() => RestrictedTimeWindowRuleValidation.Validate(resource));
        ex.Message.ShouldBe("SeasonToDay must be between 1 and 31.");
    }

    [Test]
    public void Validate_EqualDailyBounds_FullDayWindow_DoesNotThrow()
    {
        var resource = ValidResource();
        resource.DailyEnd = resource.DailyStart;

        Should.NotThrow(() => RestrictedTimeWindowRuleValidation.Validate(resource));
    }

    [Test]
    public void Validate_WrappingDailyWindow_DoesNotThrow()
    {
        var resource = ValidResource();
        resource.DailyStart = new TimeOnly(22, 0);
        resource.DailyEnd = new TimeOnly(6, 0);

        Should.NotThrow(() => RestrictedTimeWindowRuleValidation.Validate(resource));
    }
}
