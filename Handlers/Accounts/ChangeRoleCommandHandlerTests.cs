// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ChangeRoleCommandHandler: the administrator-role lock is taken inside the transaction and before the
/// role is touched, only when the Admin role is being removed (any casing); granting Admin and changing other roles
/// never take it; a refusal of the service (last active administrator) reaches the caller as a ConflictException with
/// the service's message, which is what the settings UI and the assistant skill both receive.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class ChangeRoleCommandHandlerTests
{
    private IAccountManagementService _accounts = null!;
    private IAdministratorRoleLock _lock = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ChangeRoleCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _accounts = Substitute.For<IAccountManagementService>();
        _accounts.ChangeRoleUserAsync(Arg.Any<ChangeRole>()).Returns(new HttpResultResource { Success = true });
        _lock = Substitute.For<IAdministratorRoleLock>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<HttpResultResource>>>())
            .Returns(call => call.ArgAt<Func<Task<HttpResultResource>>>(0)());
        _sut = new ChangeRoleCommandHandler(_accounts, _lock, _unitOfWork, Substitute.For<ILogger<ChangeRoleCommandHandler>>());
    }

    [TestCase("Admin")]
    [TestCase("admin")]
    public async Task RemovingAdmin_TakesTheLockInsideTheTransaction_BeforeTheRoleIsChanged(string roleName)
    {
        using var cts = new CancellationTokenSource();

        await _sut.Handle(Command(roleName, false), cts.Token);

        Received.InOrder(() =>
        {
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<HttpResultResource>>>());
            _lock.AcquireAsync(cts.Token);
            _accounts.ChangeRoleUserAsync(Arg.Any<ChangeRole>());
        });
    }

    [TestCase("Admin", true)]
    [TestCase("Authorised", false)]
    [TestCase("Authorised", true)]
    public async Task GrantingAdmin_OrChangingAnotherRole_DoesNotTakeTheLock(string roleName, bool isSelected)
    {
        await _sut.Handle(Command(roleName, isSelected), CancellationToken.None);

        await _lock.DidNotReceive().AcquireAsync(Arg.Any<CancellationToken>());
        await _accounts.Received(1).ChangeRoleUserAsync(Arg.Any<ChangeRole>());
    }

    [Test]
    public async Task RefusedByTheService_IsThrownAsConflictWithTheMessage()
    {
        _accounts.ChangeRoleUserAsync(Arg.Any<ChangeRole>())
            .Returns(new HttpResultResource { Success = false, Messages = LastAdministratorRule.RefusalMessage });

        var thrown = await Should.ThrowAsync<ConflictException>(
            () => _sut.Handle(Command(Roles.Admin, false), CancellationToken.None));

        thrown.Message.ShouldBe(LastAdministratorRule.RefusalMessage);
    }

    private static ChangeRoleCommand Command(string roleName, bool isSelected) =>
        new(new ChangeRole { UserId = "u1", RoleName = roleName, IsSelected = isSelected });
}
