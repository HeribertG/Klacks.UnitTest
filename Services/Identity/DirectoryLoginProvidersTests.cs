// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Which identity providers can serve directory sign-in (DirectoryLoginProviders.IsUsableForLogin): a directory server
/// marked for authentication with or without SSL, a company directory marked for authentication only with SSL, never a
/// redirect-based provider and never a provider not marked for authentication.
/// </summary>

using Klacks.Api.Application.Services.Identity;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Services.Identity;

[TestFixture]
public class DirectoryLoginProvidersTests
{
    [TestCase(IdentityProviderType.Ldap, true, false, true)]
    [TestCase(IdentityProviderType.Ldap, true, true, true)]
    [TestCase(IdentityProviderType.ActiveDirectory, true, true, true)]
    [TestCase(IdentityProviderType.ActiveDirectory, true, false, false)]
    [TestCase(IdentityProviderType.Ldap, false, true, false)]
    [TestCase(IdentityProviderType.ActiveDirectory, false, true, false)]
    [TestCase(IdentityProviderType.OAuth2, true, true, false)]
    [TestCase(IdentityProviderType.OpenIdConnect, true, true, false)]
    public void IsUsableForLogin_FollowsTypeAuthenticationFlagAndSsl(
        IdentityProviderType type, bool useForAuthentication, bool useSsl, bool expected)
    {
        var provider = new IdentityProvider { Type = type, UseForAuthentication = useForAuthentication, UseSsl = useSsl };

        DirectoryLoginProviders.IsUsableForLogin(provider).ShouldBe(expected);
    }

    [TestCase(IdentityProviderType.Ldap, true)]
    [TestCase(IdentityProviderType.ActiveDirectory, true)]
    [TestCase(IdentityProviderType.OAuth2, false)]
    [TestCase(IdentityProviderType.OpenIdConnect, false)]
    public void IsDirectoryType_IsTrueForTheTwoDirectoryKindsOnly(IdentityProviderType type, bool expected)
    {
        DirectoryLoginProviders.IsDirectoryType(type).ShouldBe(expected);
    }
}
