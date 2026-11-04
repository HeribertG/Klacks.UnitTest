// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Evaluates the real /mcp endpoint policy (McpEndpointExtensions.ConfigureMcpPolicy) through a
/// DefaultAuthorizationService with SeedAdminSetupGateAuthorizationHandler registered: the seeded admin is
/// denied while the own-admin setup gate is active and allowed afterwards, every other user is unaffected.
/// Covers JWT and personal access token principals alike, because the requirement reads only the
/// NameIdentifier claim of the merged principal.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpSeedAdminSetupGatePolicyTests
{
    private IAdminSetupGateService _gateService = null!;
    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;
    private IAuthorizationService _authorizationService = null!;
    private AuthorizationPolicy _policy = null!;

    [SetUp]
    public void SetUp()
    {
        _gateService = Substitute.For<IAdminSetupGateService>();
        _gateService.IsGateActiveAsync().Returns(true);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddScoped(_ => _gateService);
        services.AddScoped<IAuthorizationHandler, SeedAdminSetupGateAuthorizationHandler>();
        _provider = services.BuildServiceProvider();
        _scope = _provider.CreateScope();
        _authorizationService = _scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        var builder = new AuthorizationPolicyBuilder();
        McpEndpointExtensions.ConfigureMcpPolicy(builder);
        _policy = builder.Build();
    }

    [TearDown]
    public void TearDown()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    [Test]
    public void Policy_ContainsSeedAdminSetupGateRequirement()
    {
        _policy.Requirements.ShouldContain(SeedAdminSetupGateRequirement.Instance);
    }

    [Test]
    public async Task SeedAdminJwt_WhileGateActive_IsDenied()
    {
        var result = await _authorizationService.AuthorizeAsync(SeedAdminPrincipal(), _policy);

        result.Succeeded.ShouldBeFalse();
    }

    [Test]
    public async Task SeedAdminPat_WhileGateActive_IsDenied()
    {
        var principal = McpTestData.PatPrincipal(
            Guid.Parse(SystemAccounts.SeedAdminUserId), Guid.NewGuid(), PersonalAccessTokenAccessMode.Write, Roles.Admin);

        var result = await _authorizationService.AuthorizeAsync(principal, _policy);

        result.Succeeded.ShouldBeFalse();
    }

    [Test]
    public async Task SeedAdmin_AfterSetup_IsAllowed()
    {
        _gateService.IsGateActiveAsync().Returns(false);

        var result = await _authorizationService.AuthorizeAsync(SeedAdminPrincipal(), _policy);

        result.Succeeded.ShouldBeTrue();
    }

    [Test]
    public async Task OtherAdmin_WhileGateActive_IsAllowedWithoutConsultingGate()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "own-admin", Roles.Admin);

        var result = await _authorizationService.AuthorizeAsync(principal, _policy);

        result.Succeeded.ShouldBeTrue();
        await _gateService.DidNotReceive().IsGateActiveAsync();
    }

    [Test]
    public async Task AnonymousPrincipal_IsDenied()
    {
        var result = await _authorizationService.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), _policy);

        result.Succeeded.ShouldBeFalse();
    }

    private static ClaimsPrincipal SeedAdminPrincipal()
    {
        return McpTestData.Principal(Guid.Parse(SystemAccounts.SeedAdminUserId), Guid.NewGuid(), "admin", Roles.Admin);
    }
}
