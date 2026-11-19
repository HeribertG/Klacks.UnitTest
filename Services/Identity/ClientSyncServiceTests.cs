// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Client import from a directory (ClientSyncService). An incomplete or an empty directory result aborts the sync
/// before anything is written, so a server size limit never ends the membership of people still employed; entries are
/// keyed on LdapUserEntry.StableId; a client still stored under its distinguished name is re-keyed instead of
/// duplicated; an entry missing from a complete list ends the membership; only enabled entries are imported (a disabled
/// entry creates no client and ends the membership of an existing one, a list of only disabled entries aborts); a
/// failure reports a fixed message, not the exception text.
/// </summary>

using Klacks.Api.Application.Services.Identity;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Services.Identity;

[TestFixture]
public class ClientSyncServiceTests
{
    private const string EntryUuid = "22222222-2222-2222-2222-222222222222";
    private const string OtherEntryUuid = "33333333-3333-3333-3333-333333333333";
    private const string DistinguishedName = "uid=anna,ou=people,dc=example,dc=com";
    private const string OtherDistinguishedName = "uid=berta,ou=people,dc=example,dc=com";
    private const string FailureText = "secret internal failure text";

    private ILdapService _ldapService = null!;
    private IClientRepository _clientRepository = null!;
    private IMembershipRepository _membershipRepository = null!;
    private IClientAddressService _clientAddressService = null!;
    private IIdentityProviderSyncLogRepository _syncLogRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IdentityProvider _provider = null!;
    private List<IdentityProviderSyncLog> _syncLogs = null!;

    [SetUp]
    public void SetUp()
    {
        _ldapService = Substitute.For<ILdapService>();
        _clientRepository = Substitute.For<IClientRepository>();
        _membershipRepository = Substitute.For<IMembershipRepository>();
        _clientAddressService = Substitute.For<IClientAddressService>();
        _syncLogRepository = Substitute.For<IIdentityProviderSyncLogRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _provider = new IdentityProvider { Id = Guid.NewGuid(), Name = "Directory", Type = IdentityProviderType.Ldap };
        _syncLogs = [];
        _syncLogRepository.GetByProviderId(_provider.Id).Returns(_ => _syncLogs);
    }

    [Test]
    public async Task SyncClientsAsync_IncompleteResult_AbortsWithoutWritingAnything()
    {
        var existing = AddExistingSyncLog(OtherEntryUuid, OtherDistinguishedName);
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, IsEnabled = true }],
            IsComplete: false));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(ClientSyncService.IncompleteResultMessage);
        _provider.LastSyncError.ShouldBe(ClientSyncService.IncompleteResultMessage);
        existing.Log.IsActiveInSource.ShouldBeTrue();
        await AssertNothingWritten();
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task SyncClientsAsync_EmptyCompleteResult_AbortsAndKeepsEveryMembership()
    {
        var existing = AddExistingSyncLog(EntryUuid, DistinguishedName);
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult([], IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(ClientSyncService.EmptyResultMessage);
        result.DeactivatedClients.ShouldBe(0);
        _provider.LastSyncError.ShouldBe(ClientSyncService.EmptyResultMessage);
        existing.Log.IsActiveInSource.ShouldBeTrue();
        existing.Client.Membership!.ValidUntil.ShouldBeNull();
        await AssertNothingWritten();
    }

    [Test]
    public async Task SyncClientsAsync_NewEntryWithEntryUuid_CreatesTheClientUnderTheEntryUuid()
    {
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, Surname = "Muster", IsEnabled = true }],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.NewClients.ShouldBe(1);
        await _clientRepository.Received(1).Add(Arg.Is<Client>(c => c.LdapExternalId == EntryUuid && c.IdentityProviderId == _provider.Id));
        await _syncLogRepository.Received(1).Add(Arg.Is<IdentityProviderSyncLog>(
            l => l.ExternalId == EntryUuid && l.ExternalDn == DistinguishedName && l.IdentityProviderId == _provider.Id));
    }

    [Test]
    public async Task SyncClientsAsync_ClientStoredUnderItsDistinguishedName_IsReKeyedNotDuplicated()
    {
        var existing = AddExistingSyncLog(DistinguishedName, DistinguishedName);
        existing.Client.LdapExternalId = DistinguishedName;
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, Surname = "Muster", IsEnabled = true }],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.NewClients.ShouldBe(0);
        result.UpdatedClients.ShouldBe(1);
        result.DeactivatedClients.ShouldBe(0);
        existing.Log.ExternalId.ShouldBe(EntryUuid);
        existing.Client.LdapExternalId.ShouldBe(EntryUuid);
        existing.Log.IsActiveInSource.ShouldBeTrue();
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
        await _clientRepository.Received(1).Put(existing.Client);
    }

    [Test]
    public async Task SyncClientsAsync_EntryMissingFromACompleteList_EndsItsMembership()
    {
        var gone = AddExistingSyncLog(OtherEntryUuid, OtherDistinguishedName);
        var present = AddExistingSyncLog(EntryUuid, DistinguishedName);
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, Surname = "Muster", IsEnabled = true }],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.DeactivatedClients.ShouldBe(1);
        gone.Log.IsActiveInSource.ShouldBeFalse();
        gone.Client.Membership!.ValidUntil.ShouldNotBeNull();
        present.Log.IsActiveInSource.ShouldBeTrue();
        present.Client.Membership!.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task SyncClientsAsync_DisabledEntry_CreatesNoClient()
    {
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [
                new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, Surname = "Muster", IsEnabled = true },
                new LdapUserEntry { EntryUuid = OtherEntryUuid, DistinguishedName = OtherDistinguishedName, Surname = "Gesperrt", IsEnabled = false },
            ],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.NewClients.ShouldBe(1);
        result.TotalProcessed.ShouldBe(1);
        await _clientRepository.Received(1).Add(Arg.Is<Client>(c => c.LdapExternalId == EntryUuid));
        await _clientRepository.DidNotReceive().Add(Arg.Is<Client>(c => c.LdapExternalId == OtherEntryUuid));
    }

    [Test]
    public async Task SyncClientsAsync_ActiveEntryDisabledInTheDirectory_EndsItsMembership()
    {
        var disabledNow = AddExistingSyncLog(OtherEntryUuid, OtherDistinguishedName);
        var present = AddExistingSyncLog(EntryUuid, DistinguishedName);
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [
                new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, Surname = "Muster", IsEnabled = true },
                new LdapUserEntry { EntryUuid = OtherEntryUuid, DistinguishedName = OtherDistinguishedName, Surname = "Gesperrt", IsEnabled = false },
            ],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.DeactivatedClients.ShouldBe(1);
        disabledNow.Log.IsActiveInSource.ShouldBeFalse();
        disabledNow.Client.Membership!.ValidUntil.ShouldNotBeNull();
        present.Client.Membership!.ValidUntil.ShouldBeNull();
    }

    [Test]
    public async Task SyncClientsAsync_OnlyDisabledEntries_AbortsLikeAnEmptyList()
    {
        var existing = AddExistingSyncLog(EntryUuid, DistinguishedName);
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, IsEnabled = false }],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(ClientSyncService.EmptyResultMessage);
        existing.Client.Membership!.ValidUntil.ShouldBeNull();
        await AssertNothingWritten();
    }

    [Test]
    public async Task SyncClientsAsync_Success_StampsTheProviderAndClearsTheLastError()
    {
        _provider.LastSyncError = "earlier failure";
        _ldapService.GetUsersAsync(_provider).Returns(new LdapUserSearchResult(
            [new LdapUserEntry { EntryUuid = EntryUuid, DistinguishedName = DistinguishedName, Surname = "Muster", IsEnabled = true }],
            IsComplete: true));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        _provider.LastSyncError.ShouldBeNull();
        _provider.LastSyncCount.ShouldBe(1);
        _provider.LastSyncTime.ShouldNotBeNull();
    }

    [Test]
    public async Task SyncClientsAsync_DirectoryFailure_ReportsAFixedMessageNotTheExceptionText()
    {
        _ldapService.GetUsersAsync(_provider).ThrowsAsync(new InvalidOperationException(FailureText));

        var result = await CreateSut().SyncClientsAsync(_provider);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(ClientSyncService.SyncFailedMessage);
        _provider.LastSyncError.ShouldBe(ClientSyncService.SyncFailedMessage);
        _provider.LastSyncError.ShouldNotContain(FailureText);
        await AssertNothingWritten();
    }

    private ClientSyncService CreateSut() =>
        new(_ldapService, _clientRepository, _membershipRepository, _clientAddressService, _syncLogRepository,
            _unitOfWork, Substitute.For<ILogger<ClientSyncService>>());

    private (IdentityProviderSyncLog Log, Client Client) AddExistingSyncLog(string externalId, string distinguishedName)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            LdapExternalId = externalId,
            IdentityProviderId = _provider.Id,
            Membership = new Membership { Id = Guid.NewGuid(), ValidFrom = DateTime.UtcNow.AddYears(-1), ValidUntil = null },
        };
        var log = new IdentityProviderSyncLog
        {
            Id = Guid.NewGuid(),
            IdentityProviderId = _provider.Id,
            ClientId = client.Id,
            ExternalId = externalId,
            ExternalDn = distinguishedName,
            LastSyncTime = DateTime.UtcNow.AddDays(-1),
            IsActiveInSource = true,
        };
        _clientRepository.Get(client.Id).Returns(client);
        _syncLogs.Add(log);
        return (log, client);
    }

    private async Task AssertNothingWritten()
    {
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
        await _membershipRepository.DidNotReceive().Add(Arg.Any<Membership>());
        await _syncLogRepository.DidNotReceive().Add(Arg.Any<IdentityProviderSyncLog>());
    }
}
