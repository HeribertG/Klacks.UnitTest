// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for DeactivateAccountCommandHandler: the administrator-role lock is taken inside the transaction and
/// before the service reads the administrator count, a lock that cannot be taken stops the deactivation (fail closed),
/// and a refusal of the service (last active administrator) reaches the caller as a ConflictException with the
/// service's message.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class DeactivateAccountCommandHandlerTests
{
    private static readonly Guid TargetId = Guid.NewGuid();
    private const string ActingAdminId = "0f9d3f1a-2b4c-4d5e-8a91-1122334455aa";

    private IAccountManagementService _accounts = null!;
    private IAdministratorRoleLock _lock = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeactivateAccountCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _accounts = Substitute.For<IAccountManagementService>();
        _accounts.DeactivateAccountUserAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(new HttpResultResource { Success = true });
        _lock = Substitute.For<IAdministratorRoleLock>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<HttpResultResource>>>())
            .Returns(call => call.ArgAt<Func<Task<HttpResultResource>>>(0)());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ActingAdminId)]))
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        _sut = new DeactivateAccountCommandHandler(
            _accounts, _lock, accessor, _unitOfWork, Substitute.For<ILogger<DeactivateAccountCommandHandler>>());
    }

    [Test]
    public async Task Deactivating_TakesTheLockInsideTheTransaction_BeforeTheServiceReadsTheCount()
    {
        using var cts = new CancellationTokenSource();

        await _sut.Handle(new DeactivateAccountCommand(TargetId), cts.Token);

        Received.InOrder(() =>
        {
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<HttpResultResource>>>());
            _lock.AcquireAsync(cts.Token);
            _accounts.DeactivateAccountUserAsync(TargetId, ActingAdminId);
        });
    }

    [Test]
    public async Task TheLockCannotBeTaken_NothingIsDeactivated()
    {
        _lock.AcquireAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("lock unavailable")));

        await Should.ThrowAsync<InvalidRequestException>(
            () => _sut.Handle(new DeactivateAccountCommand(TargetId), CancellationToken.None));

        await _accounts.DidNotReceive().DeactivateAccountUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task RefusedByTheService_IsThrownAsConflictWithTheMessage()
    {
        _accounts.DeactivateAccountUserAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(new HttpResultResource { Success = false, Messages = LastAdministratorRule.DeactivationRefusalMessage });

        var thrown = await Should.ThrowAsync<ConflictException>(
            () => _sut.Handle(new DeactivateAccountCommand(TargetId), CancellationToken.None));

        thrown.Message.ShouldBe(LastAdministratorRule.DeactivationRefusalMessage);
    }
}
