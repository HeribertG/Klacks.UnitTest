// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The fixed authentication mode of LdapService: with SSL a simple bind over TLS for both kinds; a company directory
/// without SSL only Negotiate for the search account and never a simple bind; a directory server without SSL a simple
/// bind. A user bind against a company directory without SSL fails with DirectoryUnavailableException before any
/// connection is opened (the host is unreachable on purpose, so any network attempt would surface differently).
/// </summary>

using System.DirectoryServices.Protocols;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Identity;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Identity;

[TestFixture]
public class LdapServiceAuthenticationModeTests
{
    [TestCase(IdentityProviderType.ActiveDirectory, false, AuthType.Negotiate)]
    [TestCase(IdentityProviderType.ActiveDirectory, true, AuthType.Basic)]
    [TestCase(IdentityProviderType.Ldap, false, AuthType.Basic)]
    [TestCase(IdentityProviderType.Ldap, true, AuthType.Basic)]
    public void ServiceAuthType_IsFixedByKindAndSsl(IdentityProviderType type, bool useSsl, AuthType expected)
    {
        LdapService.ServiceAuthType(new IdentityProvider { Type = type, UseSsl = useSsl }).ShouldBe(expected);
    }

    [Test]
    public void ServiceAuthType_CompanyDirectoryWithoutSsl_IsNeverASimpleBind()
    {
        LdapService.ServiceAuthType(new IdentityProvider { Type = IdentityProviderType.ActiveDirectory, UseSsl = false })
            .ShouldNotBe(AuthType.Basic);
    }

    [Test]
    public async Task BindAsUser_CompanyDirectoryWithoutSsl_FailsFastWithDirectoryUnavailable()
    {
        var provider = new IdentityProvider
        {
            Type = IdentityProviderType.ActiveDirectory,
            UseSsl = false,
            Host = "invalid.invalid",
            Port = 389,
        };
        var sut = new LdapService(Substitute.For<ILogger<LdapService>>());

        await Should.ThrowAsync<DirectoryUnavailableException>(
            () => sut.BindAsUserAsync(provider, "CN=Anna,DC=example,DC=com", "secret"));
    }
}
