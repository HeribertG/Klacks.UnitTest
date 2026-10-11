// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for AddExternalHttpClients: the named Nominatim and routing clients must carry the Klacks
/// User-Agent, because the public OSRM and Nominatim servers answer requests without one with HTTP 403.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Settings.GeoServices;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Extensions;

[TestFixture]
public class ExternalHttpClientServiceCollectionExtensionsTests
{
    [TestCase(ExternalHttpClientConstants.RoutingClientName)]
    [TestCase(ExternalHttpClientConstants.NominatimClientName)]
    public void AddExternalHttpClients_NamedClient_CarriesKlacksUserAgent(string clientName)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddExternalHttpClients();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        using var client = factory.CreateClient(clientName);

        // Assert
        client.DefaultRequestHeaders.UserAgent.ToString().ShouldBe(ExternalHttpClientConstants.UserAgent);
    }

    [TestCase(ExternalHttpClientConstants.RoutingClientName, GeoServiceDefaults.OsrmBaseUrl)]
    [TestCase(ExternalHttpClientConstants.RoutingClientName, GeoServiceDefaults.OpenRouteServiceBaseUrl)]
    [TestCase(ExternalHttpClientConstants.NominatimClientName, GeoServiceDefaults.NominatimBaseUrl)]
    public async Task AddExternalHttpClients_NoOutboundTraffic_BlocksPublicHostsWithoutNetwork(string clientName, string url)
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<GeoServicesOptions>(options => options.NoOutboundTraffic = true);
        services.AddExternalHttpClients();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(clientName);

        // Act
        using var response = await client.GetAsync(url);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public void AddExternalHttpClients_UnnamedClient_HasNoUserAgent()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddExternalHttpClients();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        using var client = factory.CreateClient();

        // Assert
        client.DefaultRequestHeaders.UserAgent.ShouldBeEmpty();
    }
}
