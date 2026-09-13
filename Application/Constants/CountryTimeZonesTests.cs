// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CountryTimeZones: IANA zone resolution per country code in both the alpha-2 and the
/// alpha-3 spelling, the countries declared as spanning several zones (which must never be given a
/// guessed zone), unknown codes and case handling.
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

    [TestCase("IN", "Asia/Kolkata")]
    [TestCase("ZA", "Africa/Johannesburg")]
    [TestCase("NZ", "Pacific/Auckland")]
    [TestCase("AR", "America/Argentina/Buenos_Aires")]
    [TestCase("CL", "America/Santiago")]
    [TestCase("CO", "America/Bogota")]
    [TestCase("PE", "America/Lima")]
    [TestCase("NG", "Africa/Lagos")]
    [TestCase("KE", "Africa/Nairobi")]
    [TestCase("PH", "Asia/Manila")]
    [TestCase("PK", "Asia/Karachi")]
    [TestCase("BD", "Asia/Dhaka")]
    [TestCase("UA", "Europe/Kyiv")]
    [TestCase("HK", "Asia/Hong_Kong")]
    [TestCase("IR", "Asia/Tehran")]
    [TestCase("IQ", "Asia/Baghdad")]
    [TestCase("NP", "Asia/Kathmandu")]
    [TestCase("LK", "Asia/Colombo")]
    [TestCase("MA", "Africa/Casablanca")]
    [TestCase("DZ", "Africa/Algiers")]
    [TestCase("TN", "Africa/Tunis")]
    [TestCase("ME", "Europe/Podgorica")]
    [TestCase("AD", "Europe/Andorra")]
    [TestCase("MC", "Europe/Monaco")]
    [TestCase("SM", "Europe/San_Marino")]
    [TestCase("MD", "Europe/Chisinau")]
    [TestCase("BY", "Europe/Minsk")]
    public void Resolve_SingleZoneCountryAddedForInternationalSales_ReturnsItsZone(string countryCode, string expectedTimeZone)
    {
        CountryTimeZones.Resolve(countryCode).ShouldBe(expectedTimeZone);
    }

    [TestCase("CHE", "Europe/Zurich")]
    [TestCase("DEU", "Europe/Berlin")]
    [TestCase("GBR", "Europe/London")]
    [TestCase("IND", "Asia/Kolkata")]
    [TestCase("jpn", "Asia/Tokyo")]
    [TestCase(" ZAF ", "Africa/Johannesburg")]
    public void Resolve_Alpha3CountryCode_ReturnsTheSameZoneAsAlpha2(string countryCode, string expectedTimeZone)
    {
        CountryTimeZones.Resolve(countryCode).ShouldBe(expectedTimeZone);
    }

    [TestCase("US")]
    [TestCase("USA")]
    [TestCase("CA")]
    [TestCase("MX")]
    [TestCase("BR")]
    [TestCase("AU")]
    [TestCase("RU")]
    [TestCase("KZ")]
    [TestCase("ID")]
    [TestCase("IDN")]
    [TestCase("id")]
    public void IsMultiZoneCountry_CountrySpanningSeveralZones_IsDeclaredAndGetsNoGuessedZone(string countryCode)
    {
        CountryTimeZones.IsMultiZoneCountry(countryCode).ShouldBeTrue();
        CountryTimeZones.Resolve(countryCode).ShouldBeNull(
            "a multi-zone country must never be given a guessed capital-city zone - that would put a " +
            "Californian or Western Australian installation hours out with no signal");
    }

    [TestCase("CH")]
    [TestCase("IN")]
    [TestCase("XX")]
    [TestCase("")]
    [TestCase(null)]
    public void IsMultiZoneCountry_SingleZoneUnknownOrBlankCode_IsFalse(string? countryCode)
    {
        CountryTimeZones.IsMultiZoneCountry(countryCode).ShouldBeFalse();
    }

    [Test]
    public void MappedAndMultiZoneCountries_AreDisjoint()
    {
        var mapped = CountryTimeZones.MappedCountryCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var both = CountryTimeZones.DeclaredMultiZoneCountryCodes
            .Where(mapped.Contains)
            .ToList();

        both.ShouldBeEmpty(
            "a country is either mapped to one nationwide zone or declared multi-zone, never both - " +
            $"otherwise it is ambiguous whether Resolve or IsMultiZoneCountry decides. Both: {string.Join(", ", both)}");
    }

    [Test]
    public void Resolve_AllMappedCountryCodes_ReturnValidIanaTimeZones()
    {
        foreach (var countryCode in CountryTimeZones.MappedCountryCodes)
        {
            var timeZoneId = CountryTimeZones.Resolve(countryCode);

            timeZoneId.ShouldNotBeNull($"country code '{countryCode}' should be mapped");
            Should.NotThrow(
                () => TimeZoneInfo.FindSystemTimeZoneById(timeZoneId),
                $"'{timeZoneId}' for country '{countryCode}' should be a valid IANA time zone");
        }
    }
}
