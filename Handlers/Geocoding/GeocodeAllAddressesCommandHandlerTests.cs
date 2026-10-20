// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests GeocodeAllAddressesCommandHandler: a disabled geocoder is reported without reading addresses or calling
/// the geocoder; otherwise every address without coordinates is geocoded with its country name, hits are written
/// back and the counts add up.
/// </summary>

using Klacks.Api.Application.Commands.Geocoding;
using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Application.Handlers.Geocoding;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Geocoding;

[TestFixture]
public class GeocodeAllAddressesCommandHandlerTests
{
    private IAddressRepository _addressRepository = null!;
    private IGeocodingService _geocodingService = null!;
    private ICountryResolver _countryResolver = null!;
    private IAddressCoordinateWriter _coordinateWriter = null!;
    private GeocodeAllAddressesCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _addressRepository = Substitute.For<IAddressRepository>();
        _geocodingService = Substitute.For<IGeocodingService>();
        _geocodingService.IsEnabled.Returns(true);
        _countryResolver = Substitute.For<ICountryResolver>();
        _coordinateWriter = Substitute.For<IAddressCoordinateWriter>();
        _handler = new GeocodeAllAddressesCommandHandler(
            _addressRepository,
            _geocodingService,
            _countryResolver,
            _coordinateWriter,
            NullLogger<GeocodeAllAddressesCommandHandler>.Instance);
    }

    [Test]
    public async Task Handle_GeocodingDisabled_ReportsDisabledAndTouchesNothing()
    {
        // Arrange
        _geocodingService.IsEnabled.Returns(false);

        // Act
        var result = await _handler.Handle(new GeocodeAllAddressesCommand(), CancellationToken.None);

        // Assert
        result.ShouldBe(GeocodeAllAddressesResult.Disabled);
        await _addressRepository.DidNotReceiveWithAnyArgs().GetWithoutCoordinatesAsync(default);
        await _geocodingService.DidNotReceiveWithAnyArgs().GeocodeAddressAsync(default!, default!, default);
        await _coordinateWriter.DidNotReceiveWithAnyArgs().UpdateCoordinatesAsync(default, default, default, default);
    }

    [Test]
    public async Task Handle_WritesHitsAndCountsMisses()
    {
        // Arrange
        var hit = new AddressWithoutCoordinates(Guid.NewGuid(), "Bahnhofstrasse 1", "8001", "Zürich", "CH");
        var miss = new AddressWithoutCoordinates(Guid.NewGuid(), "Nowhere 9", "9999", "Nirgendwo", "CH");
        _addressRepository.GetWithoutCoordinatesAsync(Arg.Any<CancellationToken>()).Returns([hit, miss]);
        _countryResolver.ResolveAsync("CH", Arg.Any<CancellationToken>()).Returns(Switzerland());
        _geocodingService.GeocodeAddressAsync("Bahnhofstrasse 1, 8001 Zürich", "Schweiz", Arg.Any<CancellationToken>())
            .Returns((47.37, 8.54));
        _geocodingService.GeocodeAddressAsync("Nowhere 9, 9999 Nirgendwo", "Schweiz", Arg.Any<CancellationToken>())
            .Returns(((double?)null, (double?)null));

        // Act
        var result = await _handler.Handle(new GeocodeAllAddressesCommand(), CancellationToken.None);

        // Assert
        result.ShouldBe(new GeocodeAllAddressesResult(false, 2, 1, 1));
        await _coordinateWriter.Received(1).UpdateCoordinatesAsync(hit.Id, 47.37, 8.54, Arg.Any<CancellationToken>());
        await _coordinateWriter.DidNotReceive().UpdateCoordinatesAsync(miss.Id, Arg.Any<double>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await _countryResolver.Received(1).ResolveAsync("CH", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnknownCountryCode_IsPassedOnUnchanged()
    {
        // Arrange
        var address = new AddressWithoutCoordinates(Guid.NewGuid(), "Main Street 1", "12345", "Springfield", "XX");
        _addressRepository.GetWithoutCoordinatesAsync(Arg.Any<CancellationToken>()).Returns([address]);
        _countryResolver.ResolveAsync("XX", Arg.Any<CancellationToken>()).Returns((Countries?)null);

        // Act
        await _handler.Handle(new GeocodeAllAddressesCommand(), CancellationToken.None);

        // Assert
        await _geocodingService.Received(1).GeocodeAddressAsync("Main Street 1, 12345 Springfield", "XX", Arg.Any<CancellationToken>());
        await _countryResolver.DidNotReceiveWithAnyArgs().GetDefaultAsync(default);
    }

    [Test]
    public async Task Handle_EmptyCountry_UsesTheDefaultCountry()
    {
        // Arrange
        var address = new AddressWithoutCoordinates(Guid.NewGuid(), "Bahnhofstrasse 1", "8001", "Zürich", string.Empty);
        _addressRepository.GetWithoutCoordinatesAsync(Arg.Any<CancellationToken>()).Returns([address]);
        _countryResolver.ResolveAsync(string.Empty, Arg.Any<CancellationToken>()).Returns((Countries?)null);
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Switzerland());

        // Act
        await _handler.Handle(new GeocodeAllAddressesCommand(), CancellationToken.None);

        // Assert
        await _geocodingService.Received(1).GeocodeAddressAsync("Bahnhofstrasse 1, 8001 Zürich", "Schweiz", Arg.Any<CancellationToken>());
    }

    private static Countries Switzerland() => new()
    {
        Abbreviation = "CH",
        Name = new MultiLanguage { De = "Schweiz", En = "Switzerland" },
    };
}
