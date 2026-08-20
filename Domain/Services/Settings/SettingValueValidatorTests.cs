// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SettingValueValidator: the ACTIVE_INDUSTRIES rule accepts empty, the custom
/// marker, a single known slug and a legacy comma-separated list of known slugs (case-insensitive,
/// trimmed), and rejects unknown slugs, custom combined with other slugs, and garbage strings. Any
/// other setting key is never validated.
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
}
