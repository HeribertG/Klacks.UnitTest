// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for GeoServicesStartupReporter: one line per geo service with its host only (never path, query or tile
/// template), Information while traffic may leave the installation, Warning for disabled services and for every line
/// under NoOutboundTraffic (production only logs from Warning upwards), and Error for an unusable configuration.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class GeoServicesStartupReporterTests
{
    private const int ReportedServiceCount = 4;

    [Test]
    public async Task Defaults_LogOneInformationLinePerService_WithHostOnly()
    {
        var logger = new RecordingLogger<GeoServicesStartupReporter>();

        await new GeoServicesStartupReporter(GeoServicesPolicyTestFactory.Default(), logger).StartAsync(CancellationToken.None);

        logger.Entries.Count.ShouldBe(ReportedServiceCount);
        logger.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Information);
        logger.Entries.ShouldContain(entry => entry.Message.Contains(nameof(GeoServiceKind.MapTiles), StringComparison.Ordinal)
                                              && entry.Message.Contains(new Uri(GeoServiceDefaults.OsmTileUrlTemplate).Host, StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(entry => !entry.Message.Contains("{z}", StringComparison.Ordinal)
                                            && !entry.Message.Contains(GeoServicePaths.NominatimSearch + "?", StringComparison.Ordinal));
    }

    [Test]
    public async Task NoOutboundWithoutOwnInstances_WarnsForTheSwitchAndEveryDisabledService()
    {
        var logger = new RecordingLogger<GeoServicesStartupReporter>();

        await new GeoServicesStartupReporter(GeoServicesPolicyTestFactory.NoOutbound(), logger).StartAsync(CancellationToken.None);

        logger.Entries.Count.ShouldBe(ReportedServiceCount + 1);
        logger.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Warning);
        logger.Entries.Count(entry => entry.Message.Contains(nameof(GeoServiceDisabledReason.NoOutboundTraffic), StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(ReportedServiceCount);
    }

    [Test]
    public async Task NoOutboundWithOwnTileServer_ReportsTheTileHostAsWarning()
    {
        var logger = new RecordingLogger<GeoServicesStartupReporter>();
        var policy = GeoServicesPolicyTestFactory.With(options =>
        {
            options.NoOutboundTraffic = true;
            options.MapTiles.UrlTemplate = "https://tiles.spitex.local:8443/{z}/{x}/{y}.png";
        });

        await new GeoServicesStartupReporter(policy, logger).StartAsync(CancellationToken.None);

        var tileLine = logger.Entries.Single(entry => entry.Message.Contains(nameof(GeoServiceKind.MapTiles), StringComparison.Ordinal));
        tileLine.Level.ShouldBe(LogLevel.Warning);
        tileLine.Message.ShouldContain("tiles.spitex.local:8443");
        tileLine.Message.ShouldNotContain("{z}");
    }

    [Test]
    public async Task InvalidUrl_IsLoggedAsError()
    {
        var logger = new RecordingLogger<GeoServicesStartupReporter>();
        var policy = GeoServicesPolicyTestFactory.With(options => options.Geocoding.BaseUrl = "https://user:secret@geo.spitex.local");

        await new GeoServicesStartupReporter(policy, logger).StartAsync(CancellationToken.None);

        var geocodingLine = logger.Entries.Single(entry => entry.Message.Contains(nameof(GeoServiceKind.Geocoding), StringComparison.Ordinal));
        geocodingLine.Level.ShouldBe(LogLevel.Error);
        geocodingLine.Message.ShouldNotContain("secret");
    }
}
