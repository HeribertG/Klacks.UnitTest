// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GetGeoServicesConfigQueryHandler: projects the real geo service policy onto the UI resource, pins the
/// JSON contract the UI is built against (camelCase keys, enum values as strings, subdomains never null) and makes
/// sure no geocoding, routing or OpenRouteService base URL ever reaches the serialized response.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Handlers.Settings.GeoServices;
using Klacks.Api.Application.Queries.Settings.GeoServices;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Settings.GeoServices;

[TestFixture]
public class GetGeoServicesConfigQueryHandlerTests
{
    private const string InternalGeocodingHost = "geocoder.internal.test";
    private const string InternalRoutingHost = "osrm.internal.test";
    private const string InternalOrsHost = "ors.internal.test";
    private const string OwnTileTemplate = "https://tiles.spitex.local/{z}/{x}/{y}.png";

    private static readonly JsonSerializerOptions ApiJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Test]
    public async Task Handle_Defaults_AllServicesOnWithPublicTiles()
    {
        var handler = new GetGeoServicesConfigQueryHandler(GeoServicesPolicyTestFactory.Default());

        var result = await handler.Handle(new GetGeoServicesConfigQuery(), CancellationToken.None);

        result.NoOutboundTraffic.ShouldBeFalse();
        result.Geocoding.Enabled.ShouldBeTrue();
        result.Geocoding.Reason.ShouldBe(GeoServiceDisabledReason.None);
        result.Routing.Enabled.ShouldBeTrue();
        result.OpenRouteService.Enabled.ShouldBeTrue();
        result.MapTiles.Enabled.ShouldBeTrue();
        result.MapTiles.UrlTemplate.ShouldBe(GeoServiceDefaults.OsmTileUrlTemplate);
        result.MapTiles.Attribution.ShouldBe(GeoServiceDefaults.OsmTileAttribution);
        result.MapTiles.MaxZoom.ShouldBe(GeoServiceDefaults.OsmTileMaxZoom);
        result.MapTiles.Subdomains.ShouldBeEmpty();
        result.ExternalMapLinksEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_NoOutboundWithoutOwnUrls_EverythingOffAndLinksHidden()
    {
        var handler = new GetGeoServicesConfigQueryHandler(GeoServicesPolicyTestFactory.NoOutbound());

        var result = await handler.Handle(new GetGeoServicesConfigQuery(), CancellationToken.None);

        result.NoOutboundTraffic.ShouldBeTrue();
        result.Geocoding.Enabled.ShouldBeFalse();
        result.Geocoding.Reason.ShouldBe(GeoServiceDisabledReason.NoOutboundTraffic);
        result.Routing.Reason.ShouldBe(GeoServiceDisabledReason.NoOutboundTraffic);
        result.OpenRouteService.Reason.ShouldBe(GeoServiceDisabledReason.NoOutboundTraffic);
        result.MapTiles.Enabled.ShouldBeFalse();
        result.MapTiles.Reason.ShouldBe(GeoServiceDisabledReason.NoOutboundTraffic);
        result.MapTiles.UrlTemplate.ShouldBeNull();
        result.ExternalMapLinksEnabled.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_OwnInstances_NeverSerializesInternalBaseUrls()
    {
        var policy = GeoServicesPolicyTestFactory.With(options =>
        {
            options.NoOutboundTraffic = true;
            options.Geocoding.BaseUrl = $"https://{InternalGeocodingHost}";
            options.Routing.BaseUrl = $"https://{InternalRoutingHost}";
            options.OpenRouteService.BaseUrl = $"https://{InternalOrsHost}/v2";
            options.MapTiles.UrlTemplate = OwnTileTemplate;
        });
        var handler = new GetGeoServicesConfigQueryHandler(policy);

        var result = await handler.Handle(new GetGeoServicesConfigQuery(), CancellationToken.None);
        var json = JsonSerializer.Serialize(result, ApiJsonOptions);

        result.Geocoding.Enabled.ShouldBeTrue();
        result.Routing.Enabled.ShouldBeTrue();
        result.OpenRouteService.Enabled.ShouldBeTrue();
        json.ShouldNotContain(InternalGeocodingHost);
        json.ShouldNotContain(InternalRoutingHost);
        json.ShouldNotContain(InternalOrsHost);
        json.ShouldContain(OwnTileTemplate);
    }

    [Test]
    public async Task Serialized_MatchesUiContract()
    {
        var policy = GeoServicesPolicyTestFactory.With(options =>
        {
            options.NoOutboundTraffic = true;
            options.Routing.BaseUrl = $"https://{InternalRoutingHost}";
            options.MapTiles.UrlTemplate = OwnTileTemplate;
        });
        var handler = new GetGeoServicesConfigQueryHandler(policy);

        var result = await handler.Handle(new GetGeoServicesConfigQuery(), CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJsonOptions));
        var root = document.RootElement;

        root.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["noOutboundTraffic", "geocoding", "routing", "openRouteService", "mapTiles", "externalMapLinksEnabled"]);
        root.GetProperty("noOutboundTraffic").GetBoolean().ShouldBeTrue();
        root.GetProperty("geocoding").EnumerateObject().Select(p => p.Name).ShouldBe(["enabled", "reason"]);
        root.GetProperty("geocoding").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        root.GetProperty("geocoding").GetProperty("reason").GetString().ShouldBe("NoOutboundTraffic");
        root.GetProperty("routing").GetProperty("enabled").GetBoolean().ShouldBeTrue();
        root.GetProperty("routing").GetProperty("reason").GetString().ShouldBe("None");
        root.GetProperty("openRouteService").GetProperty("reason").GetString().ShouldBe("NoOutboundTraffic");

        var mapTiles = root.GetProperty("mapTiles");
        mapTiles.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["enabled", "reason", "urlTemplate", "subdomains", "attribution", "maxZoom"]);
        mapTiles.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        mapTiles.GetProperty("reason").GetString().ShouldBe("None");
        mapTiles.GetProperty("urlTemplate").GetString().ShouldBe(OwnTileTemplate);
        mapTiles.GetProperty("subdomains").ValueKind.ShouldBe(JsonValueKind.Array);
        mapTiles.GetProperty("subdomains").GetArrayLength().ShouldBe(0);
        mapTiles.GetProperty("attribution").GetString().ShouldBe(GeoServiceDefaults.OsmTileAttribution);
        mapTiles.GetProperty("maxZoom").GetInt32().ShouldBe(GeoServiceDefaults.OsmTileMaxZoom);
        root.GetProperty("externalMapLinksEnabled").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task Serialized_DisabledTiles_HaveNullTemplateAndEmptySubdomains()
    {
        var handler = new GetGeoServicesConfigQueryHandler(GeoServicesPolicyTestFactory.NoOutbound());

        var result = await handler.Handle(new GetGeoServicesConfigQuery(), CancellationToken.None);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, ApiJsonOptions));
        var mapTiles = document.RootElement.GetProperty("mapTiles");

        mapTiles.GetProperty("urlTemplate").ValueKind.ShouldBe(JsonValueKind.Null);
        mapTiles.GetProperty("subdomains").ValueKind.ShouldBe(JsonValueKind.Array);
        mapTiles.GetProperty("reason").GetString().ShouldBe("NoOutboundTraffic");
    }
}
