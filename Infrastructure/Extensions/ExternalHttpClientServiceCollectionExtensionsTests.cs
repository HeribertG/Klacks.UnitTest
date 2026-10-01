// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for AddExternalHttpClients: the named Nominatim and routing clients must carry the Klacks
/// User-Agent, because the public OSRM and Nominatim servers answer requests without one with HTTP 403.
/// </summary>

using Klacks.Api.Domain.Constants;
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
