// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The LDAP search filters for directory sign-in and the approval list. The base filter is the configured user filter
/// (wrapped in parentheses when it is not) or the default of the provider type; the login filter matches the typed name
/// on the login attributes of the type (directory server: uid, mail; company directory: sAMAccountName,
/// userPrincipalName, mail); the listing filter matches a substring on the listing attributes; the external-id filter
/// addresses one entry by objectGUID (company directory, bytes in Guid.ToByteArray order) or entryUUID (directory
/// server). Typed text is always escaped, so it can never add filter syntax. The expected strings are frozen here on
/// purpose: they are what the directory server receives.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Identity;

namespace Klacks.UnitTest.Infrastructure.Services.Identity;

[TestFixture]
public class LdapUserFiltersTests
{
    private const string DefaultLdapFilter = "(objectClass=person)";
    private const string ExcludeSystemAccounts = "(!(isCriticalSystemObject=TRUE))";
    private const string DefaultActiveDirectoryFilter =
        "(&(&(objectCategory=person)(objectClass=user))" + ExcludeSystemAccounts + ")";
    private const string GuidText = "00112233-4455-6677-8899-aabbccddeeff";
    private const string GuidAsDirectoryBytes = "\\33\\22\\11\\00\\55\\44\\77\\66\\88\\99\\aa\\bb\\cc\\dd\\ee\\ff";
    private const string DistinguishedName = "uid=anna,ou=people,dc=example,dc=com";

    private static IdentityProvider Provider(IdentityProviderType type, string? userFilter = null) =>
        new() { Type = type, UserFilter = userFilter };

    [Test]
    public void Base_WithoutConfiguredFilter_UsesTheDefaultOfTheType()
    {
        LdapUserFilters.Base(Provider(IdentityProviderType.Ldap)).ShouldBe(DefaultLdapFilter);
        LdapUserFilters.Base(Provider(IdentityProviderType.ActiveDirectory)).ShouldBe(DefaultActiveDirectoryFilter);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Base_WithBlankFilter_UsesTheDefaultOfTheType(string blank)
    {
        LdapUserFilters.Base(Provider(IdentityProviderType.Ldap, blank)).ShouldBe(DefaultLdapFilter);
    }

    [Test]
    public void Base_CompanyDirectoryWithConfiguredFilter_StillExcludesTheSystemAccounts()
    {
        LdapUserFilters.Base(Provider(IdentityProviderType.ActiveDirectory, "objectClass=user"))
            .ShouldBe("(&(objectClass=user)" + ExcludeSystemAccounts + ")");
    }

    [Test]
    public void Base_DirectoryServer_NeverAddsTheSystemAccountClause()
    {
        LdapUserFilters.Base(Provider(IdentityProviderType.Ldap)).ShouldNotContain("isCriticalSystemObject");
    }

    [Test]
    public void Base_ConfiguredFilterInParentheses_IsKeptAsIs()
    {
        LdapUserFilters.Base(Provider(IdentityProviderType.Ldap, "(objectClass=inetOrgPerson)"))
            .ShouldBe("(objectClass=inetOrgPerson)");
    }

    [Test]
    public void Base_ConfiguredFilterWithoutParentheses_IsWrapped()
    {
        LdapUserFilters.Base(Provider(IdentityProviderType.Ldap, "objectClass=inetOrgPerson"))
            .ShouldBe("(objectClass=inetOrgPerson)");
    }

    [Test]
    public void ForLogin_DirectoryServer_MatchesUidAndMail()
    {
        LdapUserFilters.ForLogin(Provider(IdentityProviderType.Ldap), "anna")
            .ShouldBe("(&(objectClass=person)(|(uid=anna)(mail=anna)))");
    }

    [Test]
    public void ForLogin_CompanyDirectory_MatchesAccountNamePrincipalNameAndMail()
    {
        LdapUserFilters.ForLogin(Provider(IdentityProviderType.ActiveDirectory), "anna@example.com")
            .ShouldBe(
                "(&" + DefaultActiveDirectoryFilter +
                "(|(sAMAccountName=anna@example.com)(userPrincipalName=anna@example.com)(mail=anna@example.com)))");
    }

    [Test]
    public void ForLogin_ConfiguredFilter_IsCombinedWithTheLoginAlternatives()
    {
        LdapUserFilters.ForLogin(Provider(IdentityProviderType.Ldap, "(objectClass=inetOrgPerson)"), "anna")
            .ShouldBe("(&(objectClass=inetOrgPerson)(|(uid=anna)(mail=anna)))");
    }

    [Test]
    public void ForLogin_TypedFilterSyntax_IsEscaped()
    {
        var filter = LdapUserFilters.ForLogin(Provider(IdentityProviderType.Ldap), "*)(uid=*");

        filter.ShouldBe("(&(objectClass=person)(|(uid=\\2a\\29\\28uid=\\2a)(mail=\\2a\\29\\28uid=\\2a)))");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ForListing_WithoutSearchTerm_IsTheBaseFilter(string? term)
    {
        LdapUserFilters.ForListing(Provider(IdentityProviderType.Ldap), term).ShouldBe(DefaultLdapFilter);
    }

    [Test]
    public void ForListing_DirectoryServer_MatchesTheSubstringOnTheListingAttributes()
    {
        var filter = LdapUserFilters.ForListing(Provider(IdentityProviderType.Ldap), "ann");

        filter.ShouldBe(
            "(&(objectClass=person)(|(cn=*ann*)(displayName=*ann*)(mail=*ann*)(uid=*ann*)(sn=*ann*)(givenName=*ann*)))");
        filter.ShouldNotContain("sAMAccountName");
    }

    [Test]
    public void ForListing_CompanyDirectory_UsesTheAccountNameInsteadOfTheUid()
    {
        var filter = LdapUserFilters.ForListing(Provider(IdentityProviderType.ActiveDirectory), "ann");

        filter.ShouldBe(
            "(&" + DefaultActiveDirectoryFilter +
            "(|(cn=*ann*)(displayName=*ann*)(mail=*ann*)(sAMAccountName=*ann*)(sn=*ann*)(givenName=*ann*)))");
        filter.ShouldNotContain("(uid=");
    }

    [Test]
    public void ForListing_TypedWildcardsAndParentheses_AreEscapedAndOnlyOurWildcardsRemain()
    {
        var filter = LdapUserFilters.ForListing(Provider(IdentityProviderType.Ldap), "a*)(b");

        filter.ShouldContain("(cn=*a\\2a\\29\\28b*)");
        filter.ShouldNotContain("*)(b");
    }

    [Test]
    public void ForListing_TrimsTheSearchTerm()
    {
        LdapUserFilters.ForListing(Provider(IdentityProviderType.Ldap), "  ann  ").ShouldContain("(cn=*ann*)");
    }

    [Test]
    public void ForExternalId_CompanyDirectoryGuid_MatchesObjectGuidByTheBytesOfTheGuid()
    {
        LdapUserFilters.ForExternalId(Provider(IdentityProviderType.ActiveDirectory), GuidText)
            .ShouldBe($"(&{DefaultActiveDirectoryFilter}(objectGUID={GuidAsDirectoryBytes}))");
    }

    [Test]
    public void ForExternalId_DirectoryServerGuid_MatchesEntryUuidByItsText()
    {
        LdapUserFilters.ForExternalId(Provider(IdentityProviderType.Ldap), GuidText)
            .ShouldBe($"(&{DefaultLdapFilter}(entryUUID={GuidText}))");
    }

    [Test]
    public void ForExternalId_DistinguishedName_IsNotAGuidSearch()
    {
        LdapUserFilters.IsDistinguishedNameId(DistinguishedName).ShouldBeTrue();
        LdapUserFilters.ForExternalId(Provider(IdentityProviderType.Ldap), DistinguishedName)
            .ShouldNotContain("entryUUID");
    }

    [Test]
    public void IsDistinguishedNameId_Guid_IsFalse()
    {
        LdapUserFilters.IsDistinguishedNameId(GuidText).ShouldBeFalse();
    }
}
