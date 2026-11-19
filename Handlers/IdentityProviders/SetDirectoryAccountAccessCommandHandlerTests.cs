// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Granting and withdrawing directory sign-in for one account (SetDirectoryAccountAccessCommandHandler). Withdrawing
/// deactivates the linked account through DeactivateAccountCommand and refuses with directoryAccountNotLinked without
/// one. Granting re-reads the entry from the directory (not found refuses), reactivates a deactivated linked account
/// only when it holds no role, moves a GUID link left on a soft-deleted provider, refuses any e-mail already used by an
/// account (no linking by e-mail), or creates a password-less account without a role from the directory names. External
/// ids are normalized the same way as LdapUserEntry.StableId, so grant and revoke match whatever GUID spelling arrives.
/// An entry without a usable e-mail cannot be granted. A provider that is not an LDAP or Active Directory provider used
/// for authentication is refused.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.IdentityProviders;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Handlers.IdentityProviders;

[TestFixture]
public class SetDirectoryAccountAccessCommandHandlerTests
{
    private const string ExternalId = "11111111-1111-1111-1111-111111111111";
    private const string Email = "anna.muster@example.com";
    private const string GeneratedUserName = "anna.muster";

    private IIdentityProviderRepository _providerRepository = null!;
    private ILdapService _ldapService = null!;
    private IAppUserReader _userReader = null!;
    private IUserManagementService _userManagementService = null!;
    private IUsernameGeneratorService _usernameGenerator = null!;
    private IUserService _userService = null!;
    private IMediator _mediator = null!;
    private IdentityProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _providerRepository = Substitute.For<IIdentityProviderRepository>();
        _ldapService = Substitute.For<ILdapService>();
        _userReader = Substitute.For<IAppUserReader>();
        _userManagementService = Substitute.For<IUserManagementService>();
        _usernameGenerator = Substitute.For<IUsernameGeneratorService>();
        _userService = Substitute.For<IUserService>();
        _userService.GetIdString().Returns("actor-id");
        _mediator = Substitute.For<IMediator>();
        _userManagementService.GetUserRolesAsync(Arg.Any<AppUser>()).Returns(new List<string>());
        _provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = "Directory",
            Type = IdentityProviderType.Ldap,
            UseForAuthentication = true,
        };
        _providerRepository.Get(_provider.Id).Returns(_provider);
        _usernameGenerator.GenerateUniqueUsernameAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(GeneratedUserName);
    }

    [Test]
    public async Task Handle_UnknownProvider_ThrowsNotFound()
    {
        var unknown = Guid.NewGuid();
        _providerRepository.Get(unknown).Returns((IdentityProvider?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(unknown, ExternalId, true), CancellationToken.None));
    }

    [TestCase(IdentityProviderType.OAuth2, true)]
    [TestCase(IdentityProviderType.OpenIdConnect, true)]
    [TestCase(IdentityProviderType.Ldap, false)]
    [TestCase(IdentityProviderType.ActiveDirectory, true)]
    public async Task Handle_ProviderNotUsableForDirectoryLogin_IsRefusedWithTheCode(IdentityProviderType type, bool usedForAuthentication)
    {
        _provider.Type = type;
        _provider.UseSsl = false;
        _provider.UseForAuthentication = usedForAuthentication;

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryProviderNotUsableForLogin");
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Handle_Revoke_DeactivatesTheLinkedAccount()
    {
        var linked = LinkedUser("0f0f0f0f-0000-0000-0000-000000000001");
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(linked);
        _userReader.FindByIdNoTrackingAsync(linked.Id, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = linked.Id, UserName = linked.UserName, IdentityProviderId = _provider.Id, DirectoryExternalId = ExternalId,
            DeactivatedAt = DateTime.UtcNow,
        });
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, false), CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<DeactivateAccountCommand>(command => command.UserId == Guid.Parse(linked.Id)), Arg.Any<CancellationToken>());
        result.MayLogin.ShouldBeFalse();
        result.UserId.ShouldBe(linked.Id);
    }

    [Test]
    public async Task Handle_Revoke_AlreadyDeactivatedAccount_SendsNoCommand()
    {
        var linked = LinkedUser("0f0f0f0f-0000-0000-0000-000000000002");
        linked.DeactivatedAt = DateTime.UtcNow.AddDays(-1);
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(linked);
        _userReader.FindByIdNoTrackingAsync(linked.Id, Arg.Any<CancellationToken>()).Returns(linked);

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, false), CancellationToken.None);

        await _mediator.DidNotReceive().Send(Arg.Any<DeactivateAccountCommand>(), Arg.Any<CancellationToken>());
        result.MayLogin.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_Revoke_WithoutLinkedAccount_IsRefusedWithTheCode()
    {
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns((AppUser?)null);
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, false), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountNotLinked");
        await _mediator.DidNotReceive().Send(Arg.Any<DeactivateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_GrantThenRevokeWithAnotherGuidSpelling_HitTheSameLink()
    {
        var spelledOnGrant = "{" + ExternalId.ToUpperInvariant() + "}";
        var spelledOnRevoke = ExternalId.ToUpperInvariant().Replace("-", string.Empty);
        var entry = Entry();
        entry.ObjectGuid = ExternalId.ToUpperInvariant();
        _ldapService.FindByExternalIdAsync(_provider, Arg.Any<string>()).Returns(entry);
        AppUser? created = null;
        _userManagementService.RegisterDirectoryUserAsync(Arg.Do<AppUser>(user => created = user))
            .Returns((true, (IdentityResult?)IdentityResult.Success));
        _userReader.FindByIdNoTrackingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => created);

        await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, spelledOnGrant, true), CancellationToken.None);

        created.ShouldNotBeNull();
        var storedLink = created!.DirectoryExternalId!;
        _userReader.FindByDirectoryLinkAsync(_provider.Id, storedLink, Arg.Any<CancellationToken>()).Returns(created);

        await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, spelledOnRevoke, false), CancellationToken.None);

        storedLink.ShouldBe(LdapUserEntry.NormalizeExternalId(spelledOnRevoke));
        await _mediator.Received(1).Send(
            Arg.Is<DeactivateAccountCommand>(command => command.UserId == Guid.Parse(created.Id)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Revoke_DirectoryUnavailable_StillDeactivatesAndAnswersFromTheAccount()
    {
        var linked = LinkedUser("0f0f0f0f-0000-0000-0000-000000000003");
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(linked);
        _userReader.FindByIdNoTrackingAsync(linked.Id, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = linked.Id, UserName = linked.UserName, DirectoryExternalId = ExternalId, DeactivatedAt = DateTime.UtcNow,
        });
        _ldapService.FindByExternalIdAsync(_provider, ExternalId)
            .ThrowsAsync(new DirectoryUnavailableException(new InvalidOperationException("down")));

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, false), CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<DeactivateAccountCommand>(command => command.UserId == Guid.Parse(linked.Id)), Arg.Any<CancellationToken>());
        result.MayLogin.ShouldBeFalse();
        result.IsInDirectory.ShouldBeFalse();
    }

    [Test]
    public async Task Handle_Grant_EntryNotInTheDirectory_IsRefusedWithTheCodeAndCreatesNothing()
    {
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns((LdapUserEntry?)null);

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountNotFound");
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Handle_Grant_NewAccount_IsCreatedPasswordLessWithoutRoleFromTheDirectoryNames()
    {
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        AppUser? created = null;
        _userManagementService.RegisterDirectoryUserAsync(Arg.Do<AppUser>(user => created = user))
            .Returns((true, (IdentityResult?)IdentityResult.Success));
        _userReader.FindByIdNoTrackingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => created);

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None);

        created.ShouldNotBeNull();
        created!.FirstName.ShouldBe("Anna");
        created.LastName.ShouldBe("Muster");
        created.UserName.ShouldBe(GeneratedUserName);
        created.Email.ShouldBe(Email);
        created.EmailConfirmed.ShouldBeTrue();
        created.IdentityProviderId.ShouldBe(_provider.Id);
        created.DirectoryExternalId.ShouldBe(ExternalId);
        created.PasswordHash.ShouldBeNull();
        await _usernameGenerator.Received(1).GenerateUniqueUsernameAsync("Anna", "Muster");
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
        await _userManagementService.DidNotReceive().ChangeUserRoleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        result.MayLogin.ShouldBeTrue();
        result.UserId.ShouldBe(created.Id);
        result.ExternalId.ShouldBe(ExternalId);
    }

    [Test]
    public async Task Handle_Grant_NewAccountWithoutSurname_UsesTheDisplayNameAsLastName()
    {
        var entry = Entry();
        entry.Surname = null;
        entry.GivenName = null;
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(entry);
        AppUser? created = null;
        _userManagementService.RegisterDirectoryUserAsync(Arg.Do<AppUser>(user => created = user))
            .Returns((true, (IdentityResult?)IdentityResult.Success));
        _userReader.FindByIdNoTrackingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => created);

        await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None);

        created.ShouldNotBeNull();
        created!.LastName.ShouldBe("Anna Muster");
        created.FirstName.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_Grant_EntryWithoutUsableEmail_IsRefusedWithTheCodeAndCreatesNothing()
    {
        var entry = Entry();
        entry.Mail = "not-an-address";
        entry.UserPrincipalName = null;
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(entry);

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountWithoutEmail");
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Handle_Grant_PrincipalNameIsUsedWhenMailIsMissing()
    {
        var entry = Entry();
        entry.Mail = null;
        entry.UserPrincipalName = "anna@corp.example";
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(entry);
        AppUser? created = null;
        _userManagementService.RegisterDirectoryUserAsync(Arg.Do<AppUser>(user => created = user))
            .Returns((true, (IdentityResult?)IdentityResult.Success));
        _userReader.FindByIdNoTrackingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => created);

        await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None);

        created.ShouldNotBeNull();
        created!.Email.ShouldBe("anna@corp.example");
    }

    [Test]
    public async Task Handle_Grant_DeactivatedLinkedAccount_IsReactivatedNotCreatedAgain()
    {
        var linked = LinkedUser("0f0f0f0f-0000-0000-0000-000000000004");
        linked.DeactivatedAt = DateTime.UtcNow.AddDays(-3);
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(linked);
        _userReader.FindByIdNoTrackingAsync(linked.Id, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = linked.Id, UserName = linked.UserName, IdentityProviderId = _provider.Id, DirectoryExternalId = ExternalId,
        });

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<ReactivateAccountCommand>(command => command.UserId == Guid.Parse(linked.Id)), Arg.Any<CancellationToken>());
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
        result.MayLogin.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_Grant_DeactivatedLinkedAccountWithRoles_IsRefusedAndNotReactivated()
    {
        var linked = LinkedUser("0f0f0f0f-0000-0000-0000-000000000009");
        linked.DeactivatedAt = DateTime.UtcNow.AddDays(-3);
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(linked);
        _userManagementService.GetUserRolesAsync(linked).Returns(new List<string> { "Admin" });

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountHasRoles");
        await _mediator.DidNotReceive().Send(Arg.Any<ReactivateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Grant_LinkLeftOnASoftDeletedProvider_IsMovedToThisProvider()
    {
        var orphan = LinkedUser("0f0f0f0f-0000-0000-0000-000000000010");
        orphan.IdentityProviderId = Guid.NewGuid();
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userReader.FindLinkedToDeletedProviderAsync(ExternalId, Arg.Any<CancellationToken>()).Returns(orphan);
        _userManagementService.UpdateUserAsync(orphan).Returns((true, (IdentityResult?)IdentityResult.Success));
        _userReader.FindByIdNoTrackingAsync(orphan.Id, Arg.Any<CancellationToken>()).Returns(orphan);

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None);

        orphan.IdentityProviderId.ShouldBe(_provider.Id);
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
        result.UserId.ShouldBe(orphan.Id);
        result.MayLogin.ShouldBeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Handle_Grant_OrphanedLinkWithRoles_IsRefusedAndNeverMoved(bool deactivated)
    {
        var orphan = LinkedUser("0f0f0f0f-0000-0000-0000-000000000011");
        var oldProviderId = Guid.NewGuid();
        orphan.IdentityProviderId = oldProviderId;
        orphan.DeactivatedAt = deactivated ? DateTime.UtcNow.AddDays(-2) : null;
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userReader.FindLinkedToDeletedProviderAsync(ExternalId, Arg.Any<CancellationToken>()).Returns(orphan);
        _userManagementService.GetUserRolesAsync(orphan).Returns(new List<string> { "Admin" });

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountHasRoles");
        orphan.IdentityProviderId.ShouldBe(oldProviderId);
        await _userManagementService.DidNotReceive().UpdateUserAsync(Arg.Any<AppUser>());
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
        await _mediator.DidNotReceive().Send(Arg.Any<ReactivateAccountCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Grant_DistinguishedNameId_NeverLooksForAnOrphanedLink()
    {
        const string distinguishedName = "uid=anna,ou=people,dc=example,dc=com";
        var entry = Entry();
        entry.ObjectGuid = null;
        _ldapService.FindByExternalIdAsync(_provider, distinguishedName).Returns(entry);
        _userManagementService.RegisterDirectoryUserAsync(Arg.Any<AppUser>())
            .Returns((true, (IdentityResult?)IdentityResult.Success));

        await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, distinguishedName, true), CancellationToken.None);

        await _userReader.DidNotReceive().FindLinkedToDeletedProviderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Grant_ActiveLinkedAccount_ChangesNothing()
    {
        var linked = LinkedUser("0f0f0f0f-0000-0000-0000-000000000005");
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userReader.FindByDirectoryLinkAsync(_provider.Id, ExternalId, Arg.Any<CancellationToken>()).Returns(linked);
        _userReader.FindByIdNoTrackingAsync(linked.Id, Arg.Any<CancellationToken>()).Returns(linked);

        var result = await CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None);

        await _mediator.DidNotReceive().Send(Arg.Any<ReactivateAccountCommand>(), Arg.Any<CancellationToken>());
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
        result.MayLogin.ShouldBeTrue();
    }

    [Test]
    public async Task Handle_Grant_EmailOfAnUnlinkedAccount_IsRefusedAndNeverLinked()
    {
        var existing = new AppUser
        {
            Id = "0f0f0f0f-0000-0000-0000-000000000006", UserName = "old.anna", Email = Email, PasswordHash = "stored-hash",
        };
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userManagementService.FindUserByEmailAsync(Email).Returns(existing);

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountEmailTaken");
        existing.IdentityProviderId.ShouldBeNull();
        existing.DirectoryExternalId.ShouldBeNull();
        await _userManagementService.DidNotReceive().UpdateUserAsync(Arg.Any<AppUser>());
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Handle_Grant_EmailOfAnAccountLinkedElsewhere_IsRefusedWithTheCode()
    {
        var other = new AppUser
        {
            Id = "0f0f0f0f-0000-0000-0000-000000000008", UserName = "other", Email = Email,
            IdentityProviderId = Guid.NewGuid(), DirectoryExternalId = "99999999-9999-9999-9999-999999999999",
        };
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userManagementService.FindUserByEmailAsync(Email).Returns(other);

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountEmailTaken");
        other.DirectoryExternalId.ShouldBe("99999999-9999-9999-9999-999999999999");
        await _userManagementService.DidNotReceive().UpdateUserAsync(Arg.Any<AppUser>());
        await _userManagementService.DidNotReceive().RegisterDirectoryUserAsync(Arg.Any<AppUser>());
    }

    [Test]
    public async Task Handle_Grant_DuplicateEmailOnCreate_IsRefusedWithTheCode()
    {
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userManagementService.RegisterDirectoryUserAsync(Arg.Any<AppUser>())
            .Returns((false, (IdentityResult?)IdentityResult.Failed(new IdentityErrorDescriber().DuplicateEmail(Email))));

        var thrown = await Should.ThrowAsync<DirectoryAccountAccessRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));

        thrown.ErrorCode.ShouldBe("directoryAccountEmailTaken");
    }

    [Test]
    public async Task Handle_Grant_OtherRegistrationFailure_IsRefusedAsARegistrationRejection()
    {
        _ldapService.FindByExternalIdAsync(_provider, ExternalId).Returns(Entry());
        _userManagementService.RegisterDirectoryUserAsync(Arg.Any<AppUser>())
            .Returns((false, (IdentityResult?)IdentityResult.Failed(new IdentityErrorDescriber().InvalidUserName("x"))));

        await Should.ThrowAsync<AccountRegistrationRejectedException>(
            () => CreateSut().Handle(new SetDirectoryAccountAccessCommand(_provider.Id, ExternalId, true), CancellationToken.None));
    }

    private SetDirectoryAccountAccessCommandHandler CreateSut() =>
        new(_providerRepository, _ldapService, _userReader, _userManagementService, _usernameGenerator, _userService, _mediator,
            Substitute.For<ILogger<SetDirectoryAccountAccessCommandHandler>>());

    private AppUser LinkedUser(string id) => new()
    {
        Id = id,
        UserName = "anna.linked",
        Email = Email,
        IdentityProviderId = _provider.Id,
        DirectoryExternalId = ExternalId,
    };

    private static LdapUserEntry Entry() => new()
    {
        ObjectGuid = ExternalId,
        DistinguishedName = "uid=anna,ou=people,dc=example,dc=com",
        Uid = "anna",
        GivenName = "Anna",
        Surname = "Muster",
        DisplayName = "Anna Muster",
        Mail = Email,
        IsEnabled = true,
    };
}
