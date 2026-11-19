// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The directory approval list (ListDirectoryAccountsQueryHandler): only an LDAP or Active Directory provider that is
/// used for authentication can be listed; every row says whether its entry is linked to an active account (and whether
/// that account holds roles) or, when unlinked, whether its e-mail is already used by a Klacks account; linked accounts the directory no longer returns are appended
/// only for an unfiltered, complete result; the list is sorted by display name; a truncated result is flagged; a
/// directory that cannot be read answers Success=false.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.IdentityProviders;
using Klacks.Api.Application.Queries.IdentityProviders;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Handlers.IdentityProviders;

[TestFixture]
public class ListDirectoryAccountsQueryHandlerTests
{
    private const string AnnaId = "11111111-1111-1111-1111-111111111111";
    private const string BertaId = "22222222-2222-2222-2222-222222222222";
    private const string GoneId = "33333333-3333-3333-3333-333333333333";

    private IIdentityProviderRepository _providerRepository = null!;
    private ILdapService _ldapService = null!;
    private IAppUserReader _userReader = null!;
    private IdentityProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _providerRepository = Substitute.For<IIdentityProviderRepository>();
        _ldapService = Substitute.For<ILdapService>();
        _userReader = Substitute.For<IAppUserReader>();
        _provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = "Directory",
            Type = IdentityProviderType.Ldap,
            UseForAuthentication = true,
        };
        _providerRepository.Get(_provider.Id).Returns(_provider);
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(new List<AppUser>());
        _userReader.GetEmailsInUseAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
        _userReader.GetUserIdsWithRolesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string>());
    }

    [Test]
    public async Task Handle_UnknownProvider_ThrowsNotFound()
    {
        var unknown = Guid.NewGuid();
        _providerRepository.Get(unknown).Returns((IdentityProvider?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => CreateSut().Handle(new ListDirectoryAccountsQuery(unknown, null), CancellationToken.None));
    }

    [TestCase(IdentityProviderType.OAuth2)]
    [TestCase(IdentityProviderType.OpenIdConnect)]
    public async Task Handle_RedirectProvider_IsRefusedWithTheCode(IdentityProviderType type)
    {
        _provider.Type = type;

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryProviderNotUsableForLogin");
        await _ldapService.DidNotReceive().SearchAccountsAsync(Arg.Any<IdentityProvider>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Test]
    public async Task Handle_CompanyDirectoryWithoutSsl_IsRefusedWithTheCode()
    {
        _provider.Type = IdentityProviderType.ActiveDirectory;
        _provider.UseSsl = false;

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryProviderNotUsableForLogin");
        await _ldapService.DidNotReceive().SearchAccountsAsync(Arg.Any<IdentityProvider>(), Arg.Any<string?>(), Arg.Any<int>());
    }

    [Test]
    public async Task Handle_DirectoryProviderNotUsedForAuthentication_IsRefusedWithTheCode()
    {
        _provider.UseForAuthentication = false;

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryProviderNotUsableForLogin");
    }

    [Test]
    public async Task Handle_SearchesWithTheSearchTermAndTheListLimit()
    {
        _ldapService.SearchAccountsAsync(_provider, "ann", DirectoryAccountLimits.ListLimit)
            .Returns(new LdapUserSearchResult([], IsComplete: true));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, "ann"), CancellationToken.None);

        result.Success.ShouldBeTrue();
        await _ldapService.Received(1).SearchAccountsAsync(_provider, "ann", DirectoryAccountLimits.ListLimit);
    }

    [Test]
    public async Task Handle_LinkedActiveAccount_RowMayLoginAndCarriesTheUserId()
    {
        var linked = new AppUser { Id = "user-anna", UserName = "anna", IdentityProviderId = _provider.Id, DirectoryExternalId = AnnaId };
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(new List<AppUser> { linked });
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        var row = result.Accounts.ShouldHaveSingleItem();
        row.ExternalId.ShouldBe(AnnaId);
        row.MayLogin.ShouldBeTrue();
        row.UserId.ShouldBe("user-anna");
        row.EmailInUse.ShouldBeFalse();
        row.HasRoles.ShouldBeFalse();
        row.IsInDirectory.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_LinkedAccountWithRoles_RowSaysSo()
    {
        var linked = new AppUser { Id = "user-anna", UserName = "anna", IdentityProviderId = _provider.Id, DirectoryExternalId = AnnaId };
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(new List<AppUser> { linked });
        _userReader.GetUserIdsWithRolesAsync(
                Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("user-anna")), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "user-anna" });
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.Accounts.ShouldHaveSingleItem().HasRoles.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_LinkedButDeactivatedAccount_RowMayNotLogin()
    {
        var linked = new AppUser
        {
            Id = "user-anna", UserName = "anna", IdentityProviderId = _provider.Id, DirectoryExternalId = AnnaId,
            DeactivatedAt = DateTime.UtcNow.AddDays(-1),
        };
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(new List<AppUser> { linked });
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        var row = result.Accounts.ShouldHaveSingleItem();
        row.MayLogin.ShouldBeFalse();
        row.UserId.ShouldBe("user-anna");
    }

    [Test]
    public async Task Handle_UnlinkedEntryWhoseEmailIsInUse_RowIsFlaggedWithoutNamingTheAccount()
    {
        _userReader.GetEmailsInUseAsync(
                Arg.Is<IReadOnlyCollection<string>>(emails => emails.Contains("anna@example.com")), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { "anna@example.com" });
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        var row = result.Accounts.ShouldHaveSingleItem();
        row.MayLogin.ShouldBeFalse();
        row.UserId.ShouldBeNull();
        row.EmailInUse.ShouldBeTrue();
        row.HasRoles.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_EntryWithoutAnyLink_RowIsPlain()
    {
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        var row = result.Accounts.ShouldHaveSingleItem();
        row.MayLogin.ShouldBeFalse();
        row.UserId.ShouldBeNull();
        row.EmailInUse.ShouldBeFalse();
        row.Email.ShouldBe("anna@example.com");
        row.LoginName.ShouldBe("anna");
    }

    [Test]
    public async Task Handle_EntryWithoutAValidEmail_RowHasNoEmail()
    {
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "not-an-address"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.Accounts.ShouldHaveSingleItem().Email.ShouldBeNull();
    }

    [Test]
    public async Task Handle_RowsAreSortedByDisplayName()
    {
        ReturnsEntries(true,
            Entry(BertaId, "Berta Beispiel", "berta", "berta@example.com"),
            Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.Accounts.Select(row => row.ExternalId).ShouldBe([AnnaId, BertaId]);
    }

    [Test]
    public async Task Handle_UnfilteredCompleteResult_AppendsLinkedActiveAccountsTheDirectoryNoLongerReturns()
    {
        var gone = new AppUser
        {
            Id = "user-gone", UserName = "gone", FirstName = "Gerd", LastName = "Gone",
            IdentityProviderId = _provider.Id, DirectoryExternalId = GoneId,
        };
        var goneDeactivated = new AppUser
        {
            Id = "user-off", UserName = "off", IdentityProviderId = _provider.Id, DirectoryExternalId = "44444444-4444-4444-4444-444444444444",
            DeactivatedAt = DateTime.UtcNow.AddDays(-2),
        };
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>())
            .Returns(new List<AppUser> { gone, goneDeactivated });
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.Accounts.Count.ShouldBe(2);
        var goneRow = result.Accounts.Single(row => row.ExternalId == GoneId);
        goneRow.IsInDirectory.ShouldBeFalse();
        goneRow.MayLogin.ShouldBeTrue();
        goneRow.UserId.ShouldBe("user-gone");
        result.Accounts.Any(row => row.UserId == "user-off").ShouldBeFalse();
    }

    [Test]
    public async Task Handle_FilteredSearch_DoesNotAppendMissingLinkedAccounts()
    {
        var gone = new AppUser { Id = "user-gone", UserName = "gone", IdentityProviderId = _provider.Id, DirectoryExternalId = GoneId };
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(new List<AppUser> { gone });
        _ldapService.SearchAccountsAsync(_provider, "anna", DirectoryAccountLimits.ListLimit).Returns(new LdapUserSearchResult(
            [Entry(AnnaId, "Anna Muster", "anna", "anna@example.com")], IsComplete: true));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, "anna"), CancellationToken.None);

        result.Accounts.ShouldHaveSingleItem().ExternalId.ShouldBe(AnnaId);
    }

    [Test]
    public async Task Handle_TruncatedResult_IsFlaggedAndDoesNotAppendMissingLinkedAccounts()
    {
        var gone = new AppUser { Id = "user-gone", UserName = "gone", IdentityProviderId = _provider.Id, DirectoryExternalId = GoneId };
        _userReader.GetByIdentityProviderAsync(_provider.Id, Arg.Any<CancellationToken>()).Returns(new List<AppUser> { gone });
        ReturnsEntries(false, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.IsTruncated.ShouldBeTrue();
        result.Accounts.ShouldHaveSingleItem().ExternalId.ShouldBe(AnnaId);
    }

    [Test]
    public async Task Handle_CompleteResult_IsNotFlaggedAsTruncated()
    {
        ReturnsEntries(true, Entry(AnnaId, "Anna Muster", "anna", "anna@example.com"));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.IsTruncated.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_DirectoryUnavailable_AnswersSuccessFalseWithoutRows()
    {
        _ldapService.SearchAccountsAsync(_provider, Arg.Any<string?>(), DirectoryAccountLimits.ListLimit)
            .ThrowsAsync(new DirectoryUnavailableException(new InvalidOperationException("down")));

        var result = await CreateSut().Handle(new ListDirectoryAccountsQuery(_provider.Id, null), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Accounts.ShouldBeEmpty();
    }

    private ListDirectoryAccountsQueryHandler CreateSut() =>
        new(_providerRepository, _ldapService, _userReader, Substitute.For<ILogger<ListDirectoryAccountsQueryHandler>>());

    private void ReturnsEntries(bool isComplete, params LdapUserEntry[] entries) =>
        _ldapService.SearchAccountsAsync(_provider, Arg.Any<string?>(), DirectoryAccountLimits.ListLimit)
            .Returns(new LdapUserSearchResult(entries, isComplete));

    private static LdapUserEntry Entry(string objectGuid, string displayName, string uid, string mail) => new()
    {
        ObjectGuid = objectGuid,
        DistinguishedName = $"uid={uid},ou=people,dc=example,dc=com",
        DisplayName = displayName,
        Uid = uid,
        Mail = mail,
        IsEnabled = true,
    };
}
