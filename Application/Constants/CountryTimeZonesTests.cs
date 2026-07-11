// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CountryTimeZones: IANA zone resolution per country code, unknown codes and case handling.
/// </summary>

using Klacks.Api.Application.Constants;

namespace Klacks.UnitTest.Application.Constants;

[TestFixture]
public class CountryTimeZonesTests
{
    [TestCase("CH", "Europe/Zurich")]
    [TestCase("DE", "Europe/Berlin")]
    [TestCase("GB", "Europe/London")]
    [TestCase("TR", "Europe/Istanbul")]
    [TestCase("SA", "Asia/Riyadh")]
    [TestCase("AE", "Asia/Dubai")]
    [TestCase("IL", "Asia/Jerusalem")]
    [TestCase("SE", "Europe/Stockholm")]
    [TestCase("NL", "Europe/Amsterdam")]
    [TestCase("CZ", "Europe/Prague")]
    [TestCase("EG", "Africa/Cairo")]
    [TestCase("VN", "Asia/Ho_Chi_Minh")]
    [TestCase("ID", "Asia/Jakarta")]
    [TestCase("KR", "Asia/Seoul")]
    public void Resolve_KnownCountryCode_ReturnsIanaTimeZone(string countryCode, string expectedTimeZone)
    {
        var result = CountryTimeZones.Resolve(countryCode);

        result.ShouldBe(expectedTimeZone);
    }

    [TestCase("gb", "Europe/London")]
    [TestCase("tr", "Europe/Istanbul")]
    [TestCase("sa", "Asia/Riyadh")]
    [TestCase("Nl", "Europe/Amsterdam")]
    public void Resolve_LowerOrMixedCaseCountryCode_ReturnsIanaTimeZone(string countryCode, string expectedTimeZone)
    {
        var result = CountryTimeZones.Resolve(countryCode);

        result.ShouldBe(expectedTimeZone);
    }

    [TestCase("XX")]
    [TestCase("ZZ")]
    [TestCase("US")]
    public void Resolve_UnknownCountryCode_ReturnsNull(string countryCode)
    {
        var result = CountryTimeZones.Resolve(countryCode);

        result.ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Resolve_NullOrWhitespaceCountryCode_ReturnsNull(string? countryCode)
    {
        var result = CountryTimeZones.Resolve(countryCode);

        result.ShouldBeNull();
    }

    [Test]
    public void Resolve_TrimsSurroundingWhitespace()
    {
        var result = CountryTimeZones.Resolve(" GB ");

        result.ShouldBe("Europe/London");
    }

    [Test]
    public void Resolve_AllMappedCountryCodes_ReturnValidIanaTimeZones()
    {
        var countryCodes = new[]
        {
            "CH", "LI", "DE", "AT", "FR", "IT",
            "BE", "NL", "LU", "DK", "NO", "SE", "FI", "IS", "IE", "GB",
            "ES", "PT", "PL", "CZ", "SK", "HU", "SI", "HR", "RO", "BG",
            "GR", "EE", "LV", "LT", "MT", "CY", "AL", "RS", "BA", "MK",
            "TR", "IL", "SA", "AE", "QA", "KW", "BH", "OM", "JO", "LB",
            "EG", "JP", "KR", "TH", "VN", "ID", "MY", "SG", "TW", "CN"
        };

        foreach (var countryCode in countryCodes)
        {
            var timeZoneId = CountryTimeZones.Resolve(countryCode);

            timeZoneId.ShouldNotBeNull($"country code '{countryCode}' should be mapped");
            Should.NotThrow(
                () => TimeZoneInfo.FindSystemTimeZoneById(timeZoneId),
                $"'{timeZoneId}' for country '{countryCode}' should be a valid IANA time zone");
        }
    }
}
