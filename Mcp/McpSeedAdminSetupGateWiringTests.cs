// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Wiring tests for the seed-admin setup gate on /mcp: every endpoint MapKlacksMcp maps combines (exactly as
/// the authorization middleware does) to a policy containing SeedAdminSetupGateRequirement, and
/// AddKlacksMcpServer registers SeedAdminSetupGateAuthorizationHandler as a scoped IAuthorizationHandler.
/// Without the handler the requirement would deny every caller; without the requirement the seed admin
/// would reach /mcp before setup.
/// </summary>

using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpSeedAdminSetupGateWiringTests
{
    [Test]
    public async Task MapKlacksMcp_EveryMcpEndpointPolicy_ContainsSeedAdminSetupGateRequirement()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddKlacksMcpServer();
        await using var app = builder.Build();

        app.MapKlacksMcp();

        var mcpEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.TrimStart('/')
                .StartsWith(McpServerConstants.RoutePattern.TrimStart('/'), StringComparison.Ordinal) == true)
            .ToList();
        mcpEndpoints.ShouldNotBeEmpty();

        var policyProvider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        foreach (var endpoint in mcpEndpoints)
        {
            var policy = await AuthorizationPolicy.CombineAsync(
                policyProvider,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());

            policy.ShouldNotBeNull(endpoint.DisplayName);
            policy.Requirements.ShouldContain(SeedAdminSetupGateRequirement.Instance, endpoint.DisplayName);
        }
    }

    [Test]
    public void AddKlacksMcpServer_RegistersSeedAdminSetupGateHandlerAsScopedAuthorizationHandler()
    {
        var services = new ServiceCollection();

        services.AddKlacksMcpServer();

        var registration = services.SingleOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IAuthorizationHandler)
            && descriptor.ImplementationType == typeof(SeedAdminSetupGateAuthorizationHandler));
        registration.ShouldNotBeNull();
        registration.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }
}
