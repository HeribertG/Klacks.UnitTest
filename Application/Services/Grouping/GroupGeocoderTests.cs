// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests GroupGeocoder: a place name is geocoded with the company default country named like every other address
/// geocoding (German name, then English name, then ISO code), and without a configured default country no country
/// is forced on the query instead of a hard-coded one.
/// </summary>

using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Interfaces.RouteOptimization;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupGeocoderTests
{
    private const string PlaceName = "Linz";

    private IGeocodingService _geocoding = null!;
    private ICountryResolver _countryResolver = null!;

    [SetUp]
    public void SetUp()
    {
        _geocoding = Substitute.For<IGeocodingService>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _geocoding.GeocodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)48.3, (double?)14.3));
    }

    [Test]
    public async Task GeocodeAsync_UsesTheCompanyDefaultCountryName()
    {
        // Arrange
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries
        {
            Id = Guid.NewGuid(),
            Abbreviation = "AT",
            Prefix = "+43",
            Name = new MultiLanguage { De = "Österreich", En = "Austria" }
        });

        // Act
        var coordinates = await new GroupGeocoder(_geocoding, _countryResolver).GeocodeAsync(PlaceName);

        // Assert
        coordinates.ShouldBe(((double?)48.3, (double?)14.3));
        await _geocoding.Received(1).GeocodeAsync(PlaceName, "Österreich", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GeocodeAsync_NoDefaultCountry_ForcesNoCountry()
    {
        // Act
        await new GroupGeocoder(_geocoding, _countryResolver).GeocodeAsync(PlaceName);

        // Assert
        await _geocoding.Received(1).GeocodeAsync(PlaceName, string.Empty, Arg.Any<CancellationToken>());
    }
}
