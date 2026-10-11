// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Checks the geo HTTP clients as the real application container (AddApplicationServices) registers them: the
/// named Nominatim and routing clients and the typed OpenRouteService client (IRoutingService) each carry the
/// outbound guard and a primary handler that never follows redirects, and with NoOutboundTraffic a request of the
/// typed OpenRouteService client to the public host is answered by the guard without any network access.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Routing;
using Klacks.Api.Domain.Models.Settings.GeoServices;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Extensions;

[TestFixture]
public class GeoHttpClientContainerTests
{
    private const string GuardReasonPhrase = "Blocked by geo service configuration";

    private static readonly string OpenRouteServiceClientName = nameof(IRoutingService);

    private static IEnumerable<string> GeoClientNames() =>
    [
        ExternalHttpClientConstants.NominatimClientName,
        ExternalHttpClientConstants.RoutingClientName,
        OpenRouteServiceClientName
    ];

    private ServiceProvider _provider = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<GeoServicesOptions>(options => options.NoOutboundTraffic = true);
        services.AddApplicationServices(configuration);
        _provider = services.BuildServiceProvider();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _provider.Dispose();
    }

    [TestCaseSource(nameof(GeoClientNames))]
    public void GeoClient_CarriesTheGuard_AndNeverFollowsRedirects(string clientName)
    {
        var handlers = HandlerChain(_provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName));

        handlers.OfType<GeoServiceOutboundGuardHandler>().ShouldHaveSingleItem();
        var primary = handlers.Last().ShouldBeOfType<SocketsHttpHandler>();
        primary.AllowAutoRedirect.ShouldBeFalse();
    }

    [Test]
    public async Task TypedOpenRouteServiceClient_NoOutboundTraffic_PublicHostIsBlockedByTheGuard()
    {
        using var client = _provider.GetRequiredService<IHttpClientFactory>().CreateClient(OpenRouteServiceClientName);

        using var response = await client.GetAsync(GeoServiceDefaults.OpenRouteServiceBaseUrl + "/v2/directions/driving-car");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.ReasonPhrase.ShouldBe(GuardReasonPhrase);
    }

    private static List<HttpMessageHandler> HandlerChain(HttpMessageHandler handler)
    {
        var chain = new List<HttpMessageHandler>();
        HttpMessageHandler? current = handler;
        while (current != null)
        {
            chain.Add(current);
            current = (current as DelegatingHandler)?.InnerHandler;
        }

        return chain;
    }
}
