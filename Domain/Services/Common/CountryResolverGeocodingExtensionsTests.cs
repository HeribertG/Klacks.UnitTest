// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the shared geocoding country naming: a known code yields its German name (then English name, then ISO
/// code), an empty or blank code the company default country, an unknown code passes unchanged, and without any
/// known country the name is empty instead of a hard-coded country.
/// </summary>

using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.Domain.Services.Common;

[TestFixture]
public class CountryResolverGeocodingExtensionsTests
{
    private ICountryResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<ICountryResolver>();
    }

    [Test]
    public async Task KnownCode_YieldsTheGermanName()
    {
        // Arrange
        _resolver.ResolveAsync("AT", Arg.Any<CancellationToken>()).Returns(Country("AT", "Österreich", "Austria"));

        // Act
        var name = await _resolver.ResolveGeocodingNameAsync("AT");

        // Assert
        name.ShouldBe("Österreich");
        await _resolver.DidNotReceiveWithAnyArgs().GetDefaultAsync(default);
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase(null)]
    public async Task EmptyCode_YieldsTheCompanyDefaultCountry(string? code)
    {
        // Arrange
        _resolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Country("FR", "Frankreich", "France"));

        // Act
        var name = await _resolver.ResolveGeocodingNameAsync(code);

        // Assert
        name.ShouldBe("Frankreich");
    }

    [Test]
    public async Task UnknownCode_IsPassedOnUnchanged_WithoutTheDefaultCountry()
    {
        // Act
        var name = await _resolver.ResolveGeocodingNameAsync("Atlantis");

        // Assert
        name.ShouldBe("Atlantis");
        await _resolver.DidNotReceiveWithAnyArgs().GetDefaultAsync(default);
    }

    [TestCase("")]
    [TestCase("  ")]
    public async Task EmptyCode_WithoutDefaultCountry_YieldsNoCountry(string code)
    {
        // Act
        var name = await _resolver.ResolveGeocodingNameAsync(code);

        // Assert
        name.ShouldBe(string.Empty);
    }

    [Test]
    public async Task DefaultGeocodingName_IsTheDefaultCountryName_OrEmpty()
    {
        // Act
        var withoutDefault = await _resolver.ResolveDefaultGeocodingNameAsync();
        _resolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Country("IT", "Italien", "Italy"));
        var withDefault = await _resolver.ResolveDefaultGeocodingNameAsync();

        // Assert
        withoutDefault.ShouldBe(string.Empty);
        withDefault.ShouldBe("Italien");
    }

    [Test]
    public void GeocodingCountryName_FallsBackToEnglishNameThenIsoCode()
    {
        // Act & Assert
        GeocodingCountryName.Of(Country("PT", null, "Portugal")).ShouldBe("Portugal");
        GeocodingCountryName.Of(Country("PT", null, null)).ShouldBe("PT");
        GeocodingCountryName.Of(null).ShouldBe(string.Empty);
    }

    private static Countries Country(string code, string? german, string? english) => new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = code,
        Prefix = "+0",
        Name = new MultiLanguage { De = german, En = english }
    };
}
