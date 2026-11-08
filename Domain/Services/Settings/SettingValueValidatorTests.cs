// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SettingValueValidator: the ACTIVE_INDUSTRIES rule accepts empty, the custom
/// marker, a single known slug and a legacy comma-separated list of known slugs (case-insensitive,
/// trimmed), and rejects unknown slugs, custom combined with other slugs, and garbage strings. The scheduling caps
/// accept empty or at least 1 and reject 0; the rest minimums accept 0. Other keys are not validated.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Settings;

namespace Klacks.UnitTest.Domain.Services.Settings;

[TestFixture]
public class SettingValueValidatorTests
{
    private SettingValueValidator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new SettingValueValidator();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(IndustrySlugs.Custom)]
    [TestCase(" CUSTOM ")]
    [TestCase(IndustrySlugs.Healthcare)]
    [TestCase(IndustrySlugs.Hospitality)]
    [TestCase("SECURITY")]
    [TestCase("healthcare,security")]
    [TestCase("hospitality,security")]
    [TestCase("Healthcare, SECURITY, logistics")]
    [TestCase(" healthcare ,security ")]
    public void Validate_ActiveIndustriesAcceptedValue_DoesNotThrow(string value)
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.ActiveIndustries, value));
    }

    [TestCase("not-a-slug")]
    [TestCase("healthcare,not-a-slug")]
    [TestCase("garbage input with spaces")]
    public void Validate_ActiveIndustriesUnknownSlug_Throws(string value)
    {
        Should.Throw<InvalidRequestException>(() => _sut.Validate(SettingKeys.ActiveIndustries, value));
    }

    [TestCase("custom,healthcare")]
    [TestCase("healthcare,custom")]
    [TestCase("custom,custom,healthcare")]
    public void Validate_ActiveIndustriesCustomCombinedWithOtherSlugs_Throws(string value)
    {
        Should.Throw<InvalidRequestException>(() => _sut.Validate(SettingKeys.ActiveIndustries, value));
    }

    [Test]
    public void Validate_OtherSettingKeyWithArbitraryValue_DoesNotThrow()
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.DefaultLanguage, "not-a-slug"));
    }

    [Test]
    public void Validate_ActiveIndustriesNullValue_IsTreatedAsEmptyAndDoesNotThrow()
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.ActiveIndustries, null!));
    }

    [TestCase("0")]
    [TestCase("1")]
    [TestCase("2")]
    [TestCase("3")]
    public void Validate_ProactiveAutonomyLevelInRange_DoesNotThrow(string value)
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.KlacksyProactiveAutonomyLevel, value));
    }

    [TestCase("-1")]
    [TestCase("4")]
    [TestCase("autonomous")]
    [TestCase("")]
    public void Validate_ProactiveAutonomyLevelOutOfRangeOrNotANumber_Throws(string value)
    {
        Should.Throw<InvalidRequestException>(
            () => _sut.Validate(SettingKeys.KlacksyProactiveAutonomyLevel, value));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("1001")]
    [TestCase(" 12345 ")]
    [TestCase("9999999")]
    public void Validate_DatevConsultantNumberEmptyOrInRange_DoesNotThrow(string value)
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.DatevConsultantNumber, value));
    }

    [TestCase("1000")]
    [TestCase("0")]
    [TestCase("10000000")]
    [TestCase("-1001")]
    [TestCase("+1001")]
    [TestCase("12A45")]
    [TestCase("1001.5")]
    [TestCase("99999999999")]
    public void Validate_DatevConsultantNumberOutOfRangeOrNotWholeNumber_Throws(string value)
    {
        Should.Throw<InvalidRequestException>(() => _sut.Validate(SettingKeys.DatevConsultantNumber, value));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("1")]
    [TestCase(" 500 ")]
    [TestCase("99999")]
    public void Validate_DatevClientNumberEmptyOrInRange_DoesNotThrow(string value)
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.DatevClientNumber, value));
    }

    [TestCase("0")]
    [TestCase("100000")]
    [TestCase("-1")]
    [TestCase("+1")]
    [TestCase("1.5")]
    [TestCase("abc")]
    [TestCase("99999999999")]
    public void Validate_DatevClientNumberOutOfRangeOrNotWholeNumber_Throws(string value)
    {
        Should.Throw<InvalidRequestException>(() => _sut.Validate(SettingKeys.DatevClientNumber, value));
    }

    [Test]
    public void Validate_DatevNumbersNullValue_IsTreatedAsEmptyAndDoesNotThrow()
    {
        Should.NotThrow(() => _sut.Validate(SettingKeys.DatevConsultantNumber, null!));
        Should.NotThrow(() => _sut.Validate(SettingKeys.DatevClientNumber, null!));
    }

    [TestCase(SettingKeys.SchedulingMaxDailyHours, "")]
    [TestCase(SettingKeys.SchedulingMaxDailyHours, "1")]
    [TestCase(SettingKeys.SchedulingMaxDailyHours, "10.5")]
    [TestCase(SettingKeys.SchedulingMaxWeeklyHours, " 50 ")]
    [TestCase(SettingKeys.SchedulingMaxConsecutiveDays, "6")]
    public void Validate_SchedulingCapEmptyOrAtLeastOne_DoesNotThrow(string key, string value)
    {
        Should.NotThrow(() => _sut.Validate(key, value));
    }

    [TestCase(SettingKeys.SchedulingMaxDailyHours, "0")]
    [TestCase(SettingKeys.SchedulingMaxDailyHours, "0.5")]
    [TestCase(SettingKeys.SchedulingMaxWeeklyHours, "0")]
    [TestCase(SettingKeys.SchedulingMaxWeeklyHours, "-1")]
    [TestCase(SettingKeys.SchedulingMaxConsecutiveDays, "0")]
    [TestCase(SettingKeys.SchedulingMaxConsecutiveDays, "abc")]
    public void Validate_SchedulingCapZeroBelowOneOrNotANumber_Throws(string key, string value)
    {
        Should.Throw<InvalidRequestException>(() => _sut.Validate(key, value));
    }

    [TestCase(SettingKeys.SchedulingMinPauseHours)]
    [TestCase(SettingKeys.SchedulingMinRestDays)]
    public void Validate_RestMinimumZero_DoesNotThrow(string key)
    {
        Should.NotThrow(() => _sut.Validate(key, "0"));
    }
}
