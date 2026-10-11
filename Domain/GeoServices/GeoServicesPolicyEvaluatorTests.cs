// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the geo service decision table (GeoServicesPolicyEvaluator / GeoServicesPolicy): every row for
/// every service, blank values from Compose, invalid URLs and URLs with credentials, case-insensitive public host
/// matching including the fully qualified form with a trailing dot, map tile
/// defaults and an unreadable configuration that must switch everything off instead of crashing.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Settings.GeoServices;
using Klacks.Api.Domain.Services.GeoServices;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Domain.GeoServices;

[TestFixture]
public class GeoServicesPolicyEvaluatorTests
{
    private static readonly GeoServiceKind[] EndpointKinds =
        [GeoServiceKind.Geocoding, GeoServiceKind.Routing, GeoServiceKind.OpenRouteService];

    private static IEnumerable<GeoServiceKind> AllKinds() =>
        [GeoServiceKind.Geocoding, GeoServiceKind.Routing, GeoServiceKind.OpenRouteService, GeoServiceKind.MapTiles];

    [TestCaseSource(nameof(AllKinds))]
    public void DisabledByOperator_IsOff_RegardlessOfUrlAndSwitch(GeoServiceKind kind)
    {
        var policy = GeoServicesPolicyTestFactory.With(o =>
        {
            SetEnabled(o, kind, false);
            SetUrl(o, kind, "https://own.example.org");
        });

        var endpoint = policy.Get(kind);

        endpoint.IsEnabled.ShouldBeFalse();
        endpoint.DisabledReason.ShouldBe(GeoServiceDisabledReason.DisabledByOperator);
        endpoint.BaseUrl.ShouldBeNull();
    }

    [TestCaseSource(nameof(AllKinds))]
    public void NoUrl_WithoutNoOutbound_UsesThePublicDefault(GeoServiceKind kind)
    {
        var endpoint = GeoServicesPolicyTestFactory.Default().Get(kind);

        endpoint.IsEnabled.ShouldBeTrue();
        endpoint.DisabledReason.ShouldBe(GeoServiceDisabledReason.None);
        endpoint.BaseUrl.ShouldBe(PublicDefault(kind));
    }

    [TestCaseSource(nameof(AllKinds))]
    public void NoUrl_WithNoOutbound_IsOff(GeoServiceKind kind)
    {
        var endpoint = GeoServicesPolicyTestFactory.NoOutbound().Get(kind);

        endpoint.IsEnabled.ShouldBeFalse();
        endpoint.DisabledReason.ShouldBe(GeoServiceDisabledReason.NoOutboundTraffic);
    }

    [TestCaseSource(nameof(AllKinds))]
    public void BlankUrlFromCompose_CountsAsNotSet(GeoServiceKind kind)
    {
        var endpoint = GeoServicesPolicyTestFactory.With(o => SetUrl(o, kind, "   ")).Get(kind);

        endpoint.IsEnabled.ShouldBeTrue();
        endpoint.BaseUrl.ShouldBe(PublicDefault(kind));
    }

    [TestCaseSource(nameof(InvalidUrlCases))]
    public void InvalidUrl_IsOff_WithAndWithoutNoOutbound(GeoServiceKind kind, string url, bool noOutbound)
    {
        var endpoint = GeoServicesPolicyTestFactory.With(o =>
        {
            o.NoOutboundTraffic = noOutbound;
            SetUrl(o, kind, url);
        }).Get(kind);

        endpoint.IsEnabled.ShouldBeFalse();
        endpoint.DisabledReason.ShouldBe(GeoServiceDisabledReason.InvalidConfiguration);
    }

    private static IEnumerable<TestCaseData> InvalidUrlCases()
    {
        foreach (var kind in AllKinds())
        {
            foreach (var noOutbound in new[] { false, true })
            {
                yield return new TestCaseData(kind, "/relative/path", noOutbound);
                yield return new TestCaseData(kind, "ftp://files.example.org", noOutbound);
                yield return new TestCaseData(kind, "not a url", noOutbound);
                yield return new TestCaseData(kind, "https://user:secret@geo.spitex.local/{z}/{x}/{y}.png", noOutbound);
                yield return new TestCaseData(kind, "https://apikey@geo.spitex.local:8080", noOutbound);
            }
        }
    }

    [TestCaseSource(nameof(PublicHostCases))]
    public void PublicHostAsOwnUrl_WithNoOutbound_IsBlocked(GeoServiceKind kind, string url)
    {
        var endpoint = GeoServicesPolicyTestFactory.With(o =>
        {
            o.NoOutboundTraffic = true;
            SetUrl(o, kind, url);
        }).Get(kind);

        endpoint.IsEnabled.ShouldBeFalse();
        endpoint.DisabledReason.ShouldBe(GeoServiceDisabledReason.PublicHostBlocked);
    }

    private static IEnumerable<TestCaseData> PublicHostCases()
    {
        foreach (var kind in AllKinds())
        {
            yield return new TestCaseData(kind, PublicDefault(kind));
            yield return new TestCaseData(kind, PublicDefault(kind)!.ToUpperInvariant());
        }

        yield return new TestCaseData(GeoServiceKind.MapTiles, "https://{s}.tile.openstreetmap.de/{z}/{x}/{y}.png");
        yield return new TestCaseData(GeoServiceKind.MapTiles, "https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png");
        yield return new TestCaseData(GeoServiceKind.MapTiles, "https://a.tile.opentopomap.org/{z}/{x}/{y}.png");
        yield return new TestCaseData(GeoServiceKind.Geocoding, "https://nominatim.openstreetmap.org./");
        yield return new TestCaseData(GeoServiceKind.Routing, "https://router.project-osrm.org.");
        yield return new TestCaseData(GeoServiceKind.MapTiles, "https://tile.openstreetmap.org./{z}/{x}/{y}.png");
    }

    [TestCaseSource(nameof(AllKinds))]
    public void PublicHostAsOwnUrl_WithoutNoOutbound_IsAllowed(GeoServiceKind kind)
    {
        var endpoint = GeoServicesPolicyTestFactory.With(o => SetUrl(o, kind, PublicDefault(kind)!)).Get(kind);

        endpoint.IsEnabled.ShouldBeTrue();
    }

    [TestCaseSource(nameof(AllKinds))]
    public void OwnHost_WithNoOutbound_IsOn(GeoServiceKind kind)
    {
        var ownUrl = kind == GeoServiceKind.MapTiles
            ? "https://tiles.spitex.local/{z}/{x}/{y}.png"
            : "http://geo.spitex.local:8080";
        var endpoint = GeoServicesPolicyTestFactory.With(o =>
        {
            o.NoOutboundTraffic = true;
            SetUrl(o, kind, ownUrl);
        }).Get(kind);

        endpoint.IsEnabled.ShouldBeTrue();
        endpoint.DisabledReason.ShouldBe(GeoServiceDisabledReason.None);
        endpoint.BaseUrl.ShouldBe(ownUrl);
    }

    [TestCaseSource(nameof(EndpointKinds))]
    public void OwnBaseUrl_TrailingSlashIsTrimmed(GeoServiceKind kind)
    {
        var endpoint = GeoServicesPolicyTestFactory.With(o => SetUrl(o, kind, "http://geo.spitex.local/api/")).Get(kind);

        endpoint.BaseUrl.ShouldBe("http://geo.spitex.local/api");
    }

    [Test]
    public void IsPublicHost_MatchesSubdomainsCaseInsensitive_ButNotLookalikes()
    {
        GeoServicesPolicyEvaluator.IsPublicHost("B.TILE.OpenStreetMap.org").ShouldBeTrue();
        GeoServicesPolicyEvaluator.IsPublicHost("openstreetmap.org").ShouldBeTrue();
        GeoServicesPolicyEvaluator.IsPublicHost("myopenstreetmap.org").ShouldBeFalse();
        GeoServicesPolicyEvaluator.IsPublicHost("tiles.spitex.local").ShouldBeFalse();
        GeoServicesPolicyEvaluator.IsPublicHost("nominatim.openstreetmap.org.").ShouldBeTrue();
        GeoServicesPolicyEvaluator.IsPublicHost("OPENSTREETMAP.ORG.").ShouldBeTrue();
        GeoServicesPolicyEvaluator.IsPublicHost("myopenstreetmap.org.").ShouldBeFalse();
    }

    [Test]
    public void MapTiles_Defaults_AreOsmWithoutSubdomains()
    {
        var tiles = GeoServicesPolicyTestFactory.Default().MapTiles;

        tiles.IsEnabled.ShouldBeTrue();
        tiles.UrlTemplate.ShouldBe(GeoServiceDefaults.OsmTileUrlTemplate);
        tiles.Subdomains.ShouldBeEmpty();
        tiles.Attribution.ShouldBe(GeoServiceDefaults.OsmTileAttribution);
        tiles.MaxZoom.ShouldBe(GeoServiceDefaults.OsmTileMaxZoom);
    }

    [Test]
    public void MapTiles_OwnServer_TakesSubdomainsAttributionAndZoom()
    {
        var tiles = GeoServicesPolicyTestFactory.With(o =>
        {
            o.MapTiles.UrlTemplate = "https://{s}.tiles.spitex.local/{z}/{x}/{y}.png";
            o.MapTiles.Subdomains = "a, b ,c";
            o.MapTiles.Attribution = "Spitex tiles";
            o.MapTiles.MaxZoom = 17;
        }).MapTiles;

        tiles.IsEnabled.ShouldBeTrue();
        tiles.Subdomains.ShouldBe(["a", "b", "c"]);
        tiles.Attribution.ShouldBe("Spitex tiles");
        tiles.MaxZoom.ShouldBe(17);
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void ExternalMapLinks_FollowTheNoOutboundSwitch(bool noOutbound, bool expected)
    {
        GeoServicesPolicyTestFactory.With(o => o.NoOutboundTraffic = noOutbound).ExternalMapLinksEnabled.ShouldBe(expected);
    }

    [TestCase("GeoServices:NoOutboundTraffic", "ture")]
    [TestCase("GeoServices:MapTiles:MaxZoom", "nineteen")]
    public void UnreadableConfiguration_SwitchesEverythingOff_InsteadOfThrowing(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
        var services = new ServiceCollection();
        services.Configure<GeoServicesOptions>(configuration.GetSection(GeoServicesOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var policy = new GeoServicesPolicy(provider.GetRequiredService<IOptions<GeoServicesOptions>>());

        policy.NoOutboundTraffic.ShouldBeTrue();
        policy.ExternalMapLinksEnabled.ShouldBeFalse();
        foreach (var kind in AllKinds())
        {
            policy.Get(kind).IsEnabled.ShouldBeFalse();
            policy.Get(kind).DisabledReason.ShouldBe(GeoServiceDisabledReason.InvalidConfiguration);
        }
    }

    [Test]
    public void EnvironmentStyleConfiguration_IsBound()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GeoServices:NoOutboundTraffic"] = "true",
                ["GeoServices:Routing:BaseUrl"] = "http://osrm:5000",
                ["GeoServices:Geocoding:BaseUrl"] = ""
            })
            .Build();
        var services = new ServiceCollection();
        services.Configure<GeoServicesOptions>(configuration.GetSection(GeoServicesOptions.SectionName));
        using var provider = services.BuildServiceProvider();

        var policy = new GeoServicesPolicy(provider.GetRequiredService<IOptions<GeoServicesOptions>>());

        policy.Routing.IsEnabled.ShouldBeTrue();
        policy.Routing.BaseUrl.ShouldBe("http://osrm:5000");
        policy.Geocoding.DisabledReason.ShouldBe(GeoServiceDisabledReason.NoOutboundTraffic);
    }

    private static string? PublicDefault(GeoServiceKind kind) => kind switch
    {
        GeoServiceKind.Geocoding => GeoServiceDefaults.NominatimBaseUrl,
        GeoServiceKind.Routing => GeoServiceDefaults.OsrmBaseUrl,
        GeoServiceKind.OpenRouteService => GeoServiceDefaults.OpenRouteServiceBaseUrl,
        _ => GeoServiceDefaults.OsmTileUrlTemplate
    };

    private static void SetEnabled(GeoServicesOptions options, GeoServiceKind kind, bool enabled)
    {
        if (kind == GeoServiceKind.MapTiles)
        {
            options.MapTiles.Enabled = enabled;
            return;
        }

        Endpoint(options, kind).Enabled = enabled;
    }

    private static void SetUrl(GeoServicesOptions options, GeoServiceKind kind, string url)
    {
        if (kind == GeoServiceKind.MapTiles)
        {
            options.MapTiles.UrlTemplate = url;
            return;
        }

        Endpoint(options, kind).BaseUrl = url;
    }

    private static GeoServiceEndpointOptions Endpoint(GeoServicesOptions options, GeoServiceKind kind) => kind switch
    {
        GeoServiceKind.Geocoding => options.Geocoding,
        GeoServiceKind.Routing => options.Routing,
        _ => options.OpenRouteService
    };
}
