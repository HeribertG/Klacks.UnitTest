// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the small pure rules of the address geocoding: the locality cache key normalizes postcode, city and
/// country so spelling variants share one Nominatim answer and is null without postcode or city; the privacy rule
/// decides on the client type (customers always, employees and external employees only when allowed); the
/// employee-address setting is on only for a value that parses as true.
/// </summary>

using Klacks.Api.Application.Services.Geocoding;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Geocoding;
using SettingRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Geocoding;

[TestFixture]
public class AddressGeocodingRulesOfThumbTests
{
    [Test]
    public void LocalityKey_NormalizesSpellingVariants()
    {
        GeocodingLocalityKey.Create(" ch", " 8001 ", "  Zürich ")
            .ShouldBe(GeocodingLocalityKey.Create("CH", "8001", "zürich"));
        GeocodingLocalityKey.Create("GB", "sw1a  1aa", "London")!.PostalCode.ShouldBe("SW1A 1AA");
        GeocodingLocalityKey.Create("DE", "10115", "Frankfurt   am Main")!.City.ShouldBe("frankfurt am main");
    }

    [TestCase("", "Zürich")]
    [TestCase("8001", " ")]
    [TestCase(null, "Zürich")]
    public void LocalityKey_WithoutPostcodeOrCity_IsNull(string? zip, string city)
    {
        GeocodingLocalityKey.Create("CH", zip, city).ShouldBeNull();
    }

    [TestCase(EntityTypeEnum.Customer, false, true)]
    [TestCase(EntityTypeEnum.Employee, false, false)]
    [TestCase(EntityTypeEnum.ExternEmp, false, false)]
    [TestCase(EntityTypeEnum.Employee, true, true)]
    [TestCase(EntityTypeEnum.ExternEmp, true, true)]
    public void Privacy_DecidesOnTheClientType(EntityTypeEnum clientType, bool employeeAddressesAllowed, bool expected)
    {
        AddressGeocodingPrivacy.MayGeocode(clientType, employeeAddressesAllowed).ShouldBe(expected);
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("yes", false)]
    [TestCase("false", false)]
    [TestCase("true", true)]
    [TestCase("True", true)]
    public async Task EmployeeAddressSetting_IsOnOnlyForTrue(string? value, bool expected)
    {
        var reader = Substitute.For<ISettingsReader>();
        reader.GetSetting(SettingKeys.GeocodingEmployeeAddressesEnabled)
            .Returns(value == null ? null : new SettingRow { Type = SettingKeys.GeocodingEmployeeAddressesEnabled, Value = value });

        var allowed = await new AddressGeocodingSettings(reader).AreEmployeeAddressesAllowedAsync();

        allowed.ShouldBe(expected);
    }
}
