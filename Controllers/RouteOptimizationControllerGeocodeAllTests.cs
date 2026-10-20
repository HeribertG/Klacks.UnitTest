// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Wire format of POST RouteOptimization/geocode-all: the controller forwards to GeocodeAllAddressesCommand and
/// answers 200 OK with {geocoded, failed, geocodingDisabled} when the geocoder is switched off and with
/// {total, geocoded, failed} otherwise. Neither shape carries the other's fields, which the onprem operator
/// procedure and existing scripts rely on.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Geocoding;
using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class RouteOptimizationControllerGeocodeAllTests
{
    private IMediator _mediator = null!;
    private RouteOptimizationController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new RouteOptimizationController(
            Substitute.For<IRouteOptimizationService>(),
            Substitute.For<IContainerAutofillService>(),
            _mediator,
            NullLogger<RouteOptimizationController>.Instance);
    }

    [Test]
    public async Task GeocodeAll_GeocodingDisabled_ReportsDisabledWithoutTotal()
    {
        // Arrange
        _mediator.Send(Arg.Any<GeocodeAllAddressesCommand>(), Arg.Any<CancellationToken>())
            .Returns(GeocodeAllAddressesResult.Disabled);

        // Act
        var result = await _controller.GeocodeAllAddresses(CancellationToken.None);

        // Assert
        using var body = Serialize(result);
        body.RootElement.GetProperty("geocodingDisabled").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("geocoded").GetInt32().ShouldBe(0);
        body.RootElement.GetProperty("failed").GetInt32().ShouldBe(0);
        body.RootElement.TryGetProperty("total", out _).ShouldBeFalse();
    }

    [Test]
    public async Task GeocodeAll_GeocodingEnabled_ReportsTotalGeocodedAndFailed()
    {
        // Arrange
        _mediator.Send(Arg.Any<GeocodeAllAddressesCommand>(), Arg.Any<CancellationToken>())
            .Returns(new GeocodeAllAddressesResult(false, 5, 3, 2));

        // Act
        var result = await _controller.GeocodeAllAddresses(CancellationToken.None);

        // Assert
        using var body = Serialize(result);
        body.RootElement.GetProperty("total").GetInt32().ShouldBe(5);
        body.RootElement.GetProperty("geocoded").GetInt32().ShouldBe(3);
        body.RootElement.GetProperty("failed").GetInt32().ShouldBe(2);
        body.RootElement.TryGetProperty("geocodingDisabled", out _).ShouldBeFalse();
    }

    private static JsonDocument Serialize(ActionResult result)
    {
        var ok = result.ShouldBeOfType<OkObjectResult>();
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
    }
}
