// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the registration of the address geocoding: the backfill flag is on by default; the worker is registered
/// (and requests count as drained) only when BackgroundServices:AddressGeocoding is on and the GeoServices
/// configuration leaves geocoding enabled (an unparseable switch counts as off, like IGeoServicesPolicy).
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Geocoding;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Klacks.UnitTest.Services.Geocoding;

[TestFixture]
public class AddressGeocodingRegistrationTests
{
    [Test]
    public void AddressGeocodingFlag_IsOnByDefault()
    {
        new BackgroundServiceOptions().AddressGeocoding.ShouldBeTrue();
    }

    [TestCase(null, null, true)]
    [TestCase("false", null, false)]
    [TestCase(null, "true", false)]
    [TestCase(null, "ture", false)]
    [TestCase(null, "false", true)]
    public void Registration_WorkerOnlyWhenFlagOnAndGeocodingEnabled(string? flag, string? noOutboundTraffic, bool expectWorker)
    {
        var settings = new Dictionary<string, string?>();
        if (flag != null)
        {
            settings["BackgroundServices:AddressGeocoding"] = flag;
        }

        if (noOutboundTraffic != null)
        {
            settings["GeoServices:NoOutboundTraffic"] = noOutboundTraffic;
        }

        var services = new ServiceCollection();
        services.AddAddressGeocodingServices(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        services.Any(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AddressGeocodingBackfillService))
            .ShouldBe(expectWorker);
        var availability = services.Single(d => d.ServiceType == typeof(AddressGeocodingBackfillAvailability));
        ((AddressGeocodingBackfillAvailability)availability.ImplementationInstance!).IsActive.ShouldBe(expectWorker);
    }
}
