// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how the MCP surface resolves a caller's access mode: the PAT claim wins, a missing or
/// unparseable claim on a PAT identity is Read (fail-closed), a login JWT keeps Write, and the claim
/// survives McpPrincipalCapper rebuilding an Admin principal.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpAccessModeResolverTests
{
    [TestCase(PersonalAccessTokenAccessMode.Read)]
    [TestCase(PersonalAccessTokenAccessMode.Write)]
    public void PatPrincipalWithModeClaim_ResolvesToClaimedMode(PersonalAccessTokenAccessMode accessMode)
    {
        var principal = McpTestData.PatPrincipal(Guid.NewGuid(), Guid.NewGuid(), accessMode);

        McpAccessModeResolver.Resolve(principal).ShouldBe(accessMode);
    }

    [Test]
    public void PatPrincipalWithoutModeClaim_ResolvesToRead()
    {
        var principal = McpTestData.PatPrincipal(Guid.NewGuid(), Guid.NewGuid(), accessMode: null);

        McpAccessModeResolver.Resolve(principal).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [TestCase("write")]
    [TestCase("1")]
    [TestCase("2")]
    [TestCase("Admin")]
    [TestCase("")]
    public void UnparseableOrNumericModeClaim_ResolvesToRead(string claimValue)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(PatConstants.AccessModeClaimType, claimValue)
            },
            PatConstants.SchemeName);

        McpAccessModeResolver.Resolve(new ClaimsPrincipal(identity)).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public void LoginJwtPrincipalWithoutModeClaim_KeepsWrite()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "alice", Roles.Authorised);

        McpAccessModeResolver.Resolve(principal).ShouldBe(PersonalAccessTokenAccessMode.Write);
    }

    [Test]
    public void NullPrincipal_ResolvesToRead()
    {
        McpAccessModeResolver.Resolve(null).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public void ModeClaimOnSecondaryIdentity_IsStillFound()
    {
        var jwtIdentity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) },
            "Bearer");
        var patIdentity = new ClaimsIdentity(
            new[] { new Claim(PatConstants.AccessModeClaimType, PersonalAccessTokenAccessMode.Read.ToString()) },
            PatConstants.SchemeName);
        var principal = new ClaimsPrincipal(new[] { jwtIdentity, patIdentity });

        McpAccessModeResolver.Resolve(principal).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public void AdminReadPatPrincipal_KeepsReadAfterPrincipalCapping()
    {
        var principal = McpTestData.PatPrincipal(
            Guid.NewGuid(), Guid.NewGuid(), PersonalAccessTokenAccessMode.Read, Roles.Admin);

        var capped = McpPrincipalCapper.CapToAuthorised(principal);

        capped.ShouldNotBeSameAs(principal);
        capped.IsInRole(Roles.Admin).ShouldBeFalse();
        McpAccessModeResolver.Resolve(capped).ShouldBe(PersonalAccessTokenAccessMode.Read);
    }

    [Test]
    public void UserContextReader_CarriesResolvedAccessMode()
    {
        var principal = McpTestData.PatPrincipal(Guid.NewGuid(), Guid.NewGuid(), PersonalAccessTokenAccessMode.Read);

        McpUserContextReader.Read(principal).AccessMode.ShouldBe(PersonalAccessTokenAccessMode.Read);
    }
}
