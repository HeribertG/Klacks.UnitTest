// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Directory sign-in of AuthenticationService.ValidateCredentialsAsync (search, then bind). Per directory provider in
/// order: the typed name is looked up with the search account, the account linked to that entry is resolved by the
/// entry's stable id (never by the typed text), a locked-out linked account is refused before any bind, the password is
/// checked by a bind with the entry's own distinguished name, a wrong password counts as a failed access on the linked
/// account only, and a right password without an active linked account ends in the coded
/// DirectoryAccessNotGrantedException after all providers were tried. A directory that cannot be reached falls through
/// to the next provider without counting a failure. An entry linked to the seeded admin is refused without bind; a
/// failed attempt the local check already counted on the same account is not counted twice; a deactivated account
/// skips the local check and the directory loop refuses it without touching any lockout counter.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Application.Validation.Accounts;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
internal class AuthenticationServiceDirectoryLoginTests
{
    private const string TypedName = "anna";
    private const string Password = "P@ssw0rt1";
    private const string StableId = "11111111-1111-1111-1111-111111111111";
    private const string SecondStableId = "22222222-2222-2222-2222-222222222222";
    private const string DistinguishedName = "uid=anna,ou=people,dc=example,dc=com";
    private const string SecondDistinguishedName = "cn=Anna,ou=staff,dc=corp,dc=example";
    private const string OtherEmail = "other@example.com";
    private const string CodedErrorCode = "directoryAccessNotGranted";

    private UserManager<AppUser> _userManager = null!;
    private IIdentityProviderRepository _identityProviderRepository = null!;
    private ILdapService _ldapService = null!;
    private IAppUserReader _appUserReader = null!;
    private List<IdentityProvider> _providers = null!;
    private AuthenticationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = CreateUserManager();
        _identityProviderRepository = Substitute.For<IIdentityProviderRepository>();
        _ldapService = Substitute.For<ILdapService>();
        _appUserReader = Substitute.For<IAppUserReader>();
        _providers = [];
        _identityProviderRepository.GetAuthenticationProviders().Returns(_ => _providers);
        _sut = CreateSut();
    }

    [TearDown]
    public void TearDown()
    {
        _userManager?.Dispose();
    }

    [Test]
    public async Task ValidateCredentials_LinkedActiveAccountAndRightPassword_SignsInTheLinkedAccount()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        var entry = AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        _ldapService.BindAsUserAsync(provider, entry.DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeTrue();
        user.ShouldBeSameAs(linked);
        await _userManager.Received(1).ResetAccessFailedCountAsync(linked);
        await _userManager.DidNotReceive().AccessFailedAsync(Arg.Any<AppUser>());
        await _ldapService.Received(1).BindAsUserAsync(provider, DistinguishedName, Password);
        await _appUserReader.Received(1).FindByDirectoryLinkAsync(provider.Id, StableId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidateCredentials_CompanyDirectoryProvider_IsUsedTheSameWay()
    {
        var provider = AddProvider(IdentityProviderType.ActiveDirectory);
        var entry = AddEntry(provider, SecondDistinguishedName, SecondStableId);
        var linked = AddLinkedUser(provider, SecondStableId);
        _ldapService.BindAsUserAsync(provider, entry.DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeTrue();
        user.ShouldBeSameAs(linked);
    }

    [Test]
    public async Task ValidateCredentials_NoDirectoryEntryForTheTypedName_IsRejectedWithoutBind()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        _ldapService.FindLoginEntryAsync(provider, TypedName).Returns((LdapUserEntry?)null);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _ldapService.DidNotReceive().BindAsUserAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>(), Arg.Any<string>());
        await _appUserReader.DidNotReceive().FindByDirectoryLinkAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _userManager.DidNotReceive().AccessFailedAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task ValidateCredentials_WrongPassword_CountsAFailedAccessOnTheLinkedAccountOnly()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        var unrelated = new AppUser { Id = "unrelated", Email = OtherEmail };
        _userManager.FindByEmailAsync(TypedName).Returns(unrelated);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(false);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _userManager.Received(1).AccessFailedAsync(linked);
        await _userManager.DidNotReceive().AccessFailedAsync(unrelated);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(linked);
    }

    [Test]
    public async Task ValidateCredentials_WrongPasswordForAnUnlinkedEntry_ThrowsNothingAndCountsNothing()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(false);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _userManager.DidNotReceive().AccessFailedAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task ValidateCredentials_LockedOutLinkedAccount_IsRefusedBeforeAnyBind()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        _userManager.IsLockedOutAsync(linked).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _ldapService.DidNotReceive().BindAsUserAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>(), Arg.Any<string>());
        await _userManager.DidNotReceive().AccessFailedAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task ValidateCredentials_RightPasswordButNoLinkedAccount_ThrowsTheCodedNotGrantedException()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(true);

        var thrown = await Should.ThrowAsync<DirectoryAccessNotGrantedException>(
            () => _sut.ValidateCredentialsAsync(TypedName, Password));

        thrown.ErrorCode.ShouldBe(CodedErrorCode);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task ValidateCredentials_RightPasswordButDeactivatedLinkedAccount_ThrowsTheCodedNotGrantedException()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        linked.DeactivatedAt = DateTime.UtcNow.AddDays(-1);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(true);

        var thrown = await Should.ThrowAsync<DirectoryAccessNotGrantedException>(
            () => _sut.ValidateCredentialsAsync(TypedName, Password));

        thrown.ErrorCode.ShouldBe(CodedErrorCode);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(linked);
        await _userManager.DidNotReceive().AccessFailedAsync(linked);
    }

    [Test]
    public async Task ValidateCredentials_TypedEmailOfAnotherAccount_NeverMapsOntoThatAccount()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId, typedName: OtherEmail);
        var other = new AppUser { Id = "other", Email = OtherEmail };
        _userManager.FindByEmailAsync(OtherEmail).Returns(other);
        _userManager.HasPasswordAsync(other).Returns(false);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(true);

        await Should.ThrowAsync<DirectoryAccessNotGrantedException>(
            () => _sut.ValidateCredentialsAsync(OtherEmail, Password));

        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(other);
    }

    [Test]
    public async Task ValidateCredentials_TypedEmailOfAnotherAccount_SignsInTheLinkedAccountNotThatAccount()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId, typedName: OtherEmail);
        var linked = AddLinkedUser(provider, StableId);
        var other = new AppUser { Id = "other", Email = OtherEmail };
        _userManager.FindByEmailAsync(OtherEmail).Returns(other);
        _userManager.HasPasswordAsync(other).Returns(false);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(OtherEmail, Password);

        isValid.ShouldBeTrue();
        user.ShouldBeSameAs(linked);
        user.ShouldNotBeSameAs(other);
    }

    [Test]
    public async Task ValidateCredentials_FirstProviderKnowsNobody_TheSecondProviderSignsIn()
    {
        var first = AddProvider(IdentityProviderType.Ldap);
        var second = AddProvider(IdentityProviderType.ActiveDirectory);
        _ldapService.FindLoginEntryAsync(first, TypedName).Returns((LdapUserEntry?)null);
        var entry = AddEntry(second, SecondDistinguishedName, SecondStableId);
        var linked = AddLinkedUser(second, SecondStableId);
        _ldapService.BindAsUserAsync(second, entry.DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeTrue();
        user.ShouldBeSameAs(linked);
        await _ldapService.DidNotReceive().BindAsUserAsync(first, Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ValidateCredentials_FirstProviderThrows_TheSecondProviderIsStillTried()
    {
        var first = AddProvider(IdentityProviderType.Ldap);
        var second = AddProvider(IdentityProviderType.ActiveDirectory);
        _ldapService.FindLoginEntryAsync(first, TypedName)
            .ThrowsAsync(new DirectoryUnavailableException(new InvalidOperationException("down")));
        var entry = AddEntry(second, SecondDistinguishedName, SecondStableId);
        var linked = AddLinkedUser(second, SecondStableId);
        _ldapService.BindAsUserAsync(second, entry.DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeTrue();
        user.ShouldBeSameAs(linked);
    }

    [Test]
    public async Task ValidateCredentials_DirectoryUnavailableDuringBind_CountsNoFailedAccess()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password)
            .ThrowsAsync(new DirectoryUnavailableException(new InvalidOperationException("down")));

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _userManager.DidNotReceive().AccessFailedAsync(linked);
    }

    [Test]
    public async Task ValidateCredentials_NotGrantedOnTheFirstProviderButGrantedOnTheSecond_SignsIn()
    {
        var first = AddProvider(IdentityProviderType.Ldap);
        var second = AddProvider(IdentityProviderType.ActiveDirectory);
        AddEntry(first, DistinguishedName, StableId);
        _ldapService.BindAsUserAsync(first, DistinguishedName, Password).Returns(true);
        var entry = AddEntry(second, SecondDistinguishedName, SecondStableId);
        var linked = AddLinkedUser(second, SecondStableId);
        _ldapService.BindAsUserAsync(second, entry.DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeTrue();
        user.ShouldBeSameAs(linked);
    }

    [Test]
    public async Task ValidateCredentials_RedirectProvidersAreNotAskedToSignInByPassword()
    {
        AddProvider(IdentityProviderType.OAuth2);
        AddProvider(IdentityProviderType.OpenIdConnect);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _ldapService.DidNotReceive().FindLoginEntryAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>());
    }

    [Test]
    public async Task ValidateCredentials_CompanyDirectoryWithoutSsl_IsNeverAsked()
    {
        var provider = AddProvider(IdentityProviderType.ActiveDirectory);
        provider.UseSsl = false;
        AddEntry(provider, SecondDistinguishedName, SecondStableId);
        AddLinkedUser(provider, SecondStableId);
        _ldapService.BindAsUserAsync(provider, SecondDistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _ldapService.DidNotReceive().FindLoginEntryAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>());
        await _ldapService.DidNotReceive().BindAsUserAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ValidateCredentials_EntryLinkedToTheSeedAdmin_IsRefusedWithoutBind()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var seedAdmin = new AppUser { Id = SystemAccounts.SeedAdminUserId, IdentityProviderId = provider.Id, DirectoryExternalId = StableId };
        _appUserReader.FindByDirectoryLinkAsync(provider.Id, StableId, Arg.Any<CancellationToken>()).Returns(seedAdmin);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _ldapService.DidNotReceive().BindAsUserAsync(Arg.Any<IdentityProvider>(), Arg.Any<string>(), Arg.Any<string>());
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task ValidateCredentials_WrongPasswordOnBothPaths_CountsTheSameAccountOnlyOnce()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        _userManager.FindByEmailAsync(TypedName).Returns(linked);
        _userManager.HasPasswordAsync(linked).Returns(true);
        _userManager.CheckPasswordAsync(linked, Password).Returns(false);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(false);

        var (isValid, _) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        await _userManager.Received(1).AccessFailedAsync(linked);
    }

    [Test]
    public async Task ValidateCredentials_DeactivatedAccountTypedByEmail_SkipsTheLocalCheckAndEndsNotGranted()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        AddEntry(provider, DistinguishedName, StableId);
        var linked = AddLinkedUser(provider, StableId);
        linked.DeactivatedAt = DateTime.UtcNow.AddDays(-1);
        _userManager.FindByEmailAsync(TypedName).Returns(linked);
        _userManager.HasPasswordAsync(linked).Returns(true);
        _ldapService.BindAsUserAsync(provider, DistinguishedName, Password).Returns(true);

        var thrown = await Should.ThrowAsync<DirectoryAccessNotGrantedException>(() => _sut.ValidateCredentialsAsync(TypedName, Password));

        thrown.ErrorCode.ShouldBe(CodedErrorCode);
        await _userManager.DidNotReceive().CheckPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>());
        await _userManager.DidNotReceive().AccessFailedAsync(Arg.Any<AppUser>());
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(Arg.Any<AppUser>());
        await _userManager.DidNotReceive().IsLockedOutAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task ValidateCredentials_DeactivatedAccountWithoutDirectoryEntry_IsRefusedGenerically()
    {
        var provider = AddProvider(IdentityProviderType.Ldap);
        _ldapService.FindLoginEntryAsync(provider, TypedName).Returns((LdapUserEntry?)null);
        var local = new AppUser { Id = "local", Email = OtherEmail, DeactivatedAt = DateTime.UtcNow.AddDays(-1) };
        _userManager.FindByEmailAsync(TypedName).Returns(local);
        _userManager.HasPasswordAsync(local).Returns(true);
        _userManager.CheckPasswordAsync(local, Password).Returns(true);

        var (isValid, user) = await _sut.ValidateCredentialsAsync(TypedName, Password);

        isValid.ShouldBeFalse();
        user.ShouldBeNull();
        await _userManager.DidNotReceive().CheckPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>());
        await _userManager.DidNotReceive().AccessFailedAsync(Arg.Any<AppUser>());
    }

    private AuthenticationService CreateSut() =>
        new(_userManager, new JwtValidator(new JwtSettings()), _identityProviderRepository, _ldapService, _appUserReader,
            Substitute.For<ILogger<AuthenticationService>>());

    private IdentityProvider AddProvider(IdentityProviderType type)
    {
        var provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = $"{type}-{Guid.NewGuid():N}",
            Type = type,
            IsEnabled = true,
            UseForAuthentication = true,
            UseSsl = type == IdentityProviderType.ActiveDirectory,
        };

        _providers.Add(provider);
        return provider;
    }

    private LdapUserEntry AddEntry(IdentityProvider provider, string distinguishedName, string stableId, string typedName = TypedName)
    {
        var entry = new LdapUserEntry { DistinguishedName = distinguishedName, ObjectGuid = stableId, IsEnabled = true };
        _ldapService.FindLoginEntryAsync(provider, typedName).Returns(entry);
        return entry;
    }

    private AppUser AddLinkedUser(IdentityProvider provider, string stableId)
    {
        var linked = new AppUser
        {
            Id = $"linked-{stableId}",
            Email = "anna@example.com",
            IdentityProviderId = provider.Id,
            DirectoryExternalId = stableId,
        };
        _appUserReader.FindByDirectoryLinkAsync(provider.Id, stableId, Arg.Any<CancellationToken>()).Returns(linked);
        _userManager.IsLockedOutAsync(linked).Returns(false);
        return linked;
    }

    private static UserManager<AppUser> CreateUserManager()
    {
        return Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(),
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(),
            new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(),
            Substitute.For<ILookupNormalizer>(),
            Substitute.For<IdentityErrorDescriber>(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());
    }
}
