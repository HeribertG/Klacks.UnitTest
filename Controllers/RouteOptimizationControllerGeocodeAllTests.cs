// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for POST RouteOptimization/geocode-all with an operator-disabled geocoder: the endpoint answers 200 OK with
/// geocodingDisabled, touches neither the database (the controller gets no DbContext at all) nor the geocoder, and
/// writes no coordinates.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Staffs;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class RouteOptimizationControllerGeocodeAllTests
{
    [Test]
    public async Task GeocodeAll_GeocodingDisabled_ReportsDisabledWithoutDatabaseOrGeocoder()
    {
        var geocodingService = Substitute.For<IGeocodingService>();
        geocodingService.IsEnabled.Returns(false);
        var coordinateWriter = Substitute.For<IAddressCoordinateWriter>();
        var controller = new RouteOptimizationController(
            Substitute.For<IRouteOptimizationService>(),
            Substitute.For<IContainerAutofillService>(),
            geocodingService,
            coordinateWriter,
            null!,
            NullLogger<RouteOptimizationController>.Instance);

        var result = await controller.GeocodeAllAddresses(CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        body.RootElement.GetProperty("geocodingDisabled").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("geocoded").GetInt32().ShouldBe(0);
        body.RootElement.GetProperty("failed").GetInt32().ShouldBe(0);
        await geocodingService.DidNotReceiveWithAnyArgs().GeocodeAddressAsync(default!, default!, default);
        await coordinateWriter.DidNotReceiveWithAnyArgs().UpdateCoordinatesAsync(default, default, default, default);
    }
}
