// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GetClientLocationsQueryHandler: with geocoding on, the marker is the city-level geocode (only city and
/// country leave the installation) even when the address has stored coordinates; when that geocode finds nothing or
/// geocoding is switched off, the stored address coordinates are used, otherwise none. Visibility scoping is unchanged.
/// </summary>

using Klacks.Api.Application.Handlers.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Dashboard;

[TestFixture]
public class GetClientLocationsQueryHandlerTests
{
    private const double StoredLatitude = 46.9480;
    private const double StoredLongitude = 7.4474;
    private const double CityLatitude = 46.95;
    private const double CityLongitude = 7.45;

    private IClientRepository _clientRepository = null!;
    private IGeocodingService _geocodingService = null!;
    private IGroupVisibilityService _groupVisibilityService = null!;
    private GetClientLocationsQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _geocodingService = Substitute.For<IGeocodingService>();
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _groupVisibilityService.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());
        _handler = new GetClientLocationsQueryHandler(
            _clientRepository, _geocodingService, _groupVisibilityService,
            NullLogger<GetClientLocationsQueryHandler>.Instance);
    }

    [Test]
    public async Task GeocodingOn_UsesTheCityGeocode_EvenWhenCoordinatesAreStored()
    {
        HaveClient(StoredLatitude, StoredLongitude);
        _geocodingService.IsEnabled.Returns(true);
        _geocodingService.GeocodeAsync("Bern", "CH", Arg.Any<CancellationToken>())
            .Returns(((double?)CityLatitude, (double?)CityLongitude));

        var address = await SingleAddressAsync();

        address.Latitude.ShouldBe(CityLatitude);
        address.Longitude.ShouldBe(CityLongitude);
    }

    [Test]
    public async Task GeocodingOn_CityNotFound_FallsBackToStoredCoordinates()
    {
        HaveClient(StoredLatitude, StoredLongitude);
        _geocodingService.IsEnabled.Returns(true);
        _geocodingService.GeocodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));

        var address = await SingleAddressAsync();

        address.Latitude.ShouldBe(StoredLatitude);
        address.Longitude.ShouldBe(StoredLongitude);
    }

    [Test]
    public async Task GeocodingOff_UsesStoredCoordinates_WithoutCallingTheGeocoder()
    {
        HaveClient(StoredLatitude, StoredLongitude);
        _geocodingService.IsEnabled.Returns(false);

        var address = await SingleAddressAsync();

        address.Latitude.ShouldBe(StoredLatitude);
        address.Longitude.ShouldBe(StoredLongitude);
        await _geocodingService.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GeocodingOff_NoStoredCoordinates_ReturnsNoCoordinates()
    {
        HaveClient(null, null);
        _geocodingService.IsEnabled.Returns(false);

        var address = await SingleAddressAsync();

        address.Latitude.ShouldBeNull();
        address.Longitude.ShouldBeNull();
        await _geocodingService.DidNotReceive().GeocodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedCallerWithoutVisibleGroups_GetsNothing()
    {
        _groupVisibilityService.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([], []));

        var result = await _handler.Handle(new GetClientLocationsQuery(), CancellationToken.None);

        result.ShouldBeEmpty();
        await _clientRepository.DidNotReceive().GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedCaller_ReadsOnlyTheVisibleRoots()
    {
        var rootId = Guid.NewGuid();
        _groupVisibilityService.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([rootId], [rootId]));
        _clientRepository.GetActiveClientsWithAddressesForGroupsAsync(Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client>());

        await _handler.Handle(new GetClientLocationsQuery(), CancellationToken.None);

        await _clientRepository.Received(1).GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Is<List<Guid>>(ids => ids.Single() == rootId), Arg.Any<CancellationToken>());
        await _clientRepository.DidNotReceive().GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>());
    }

    private void HaveClient(double? latitude, double? longitude)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Addresses =
            [
                new Address
                {
                    City = "Bern",
                    Country = "CH",
                    Zip = "3011",
                    ValidFrom = DateTime.UtcNow.AddDays(-1),
                    Latitude = latitude,
                    Longitude = longitude
                }
            ]
        };
        _clientRepository.GetActiveClientsWithAddressesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Client> { client });
    }

    private async Task<Klacks.Api.Application.DTOs.Dashboard.AddressInfo> SingleAddressAsync()
    {
        var result = (await _handler.Handle(new GetClientLocationsQuery(), CancellationToken.None)).ToList();
        result.Count.ShouldBe(1);
        return result[0].CurrentAddress!;
    }
}
