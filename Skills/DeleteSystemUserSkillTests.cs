// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for delete_system_user: the deactivation runs inside a transaction that first takes the
/// administrator-role lock, a lock that cannot be taken stops it (fail closed), and the refusal for the last active
/// administrator comes back as an error that names no success.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteSystemUserSkillTests
{
    private static readonly Guid TargetId = Guid.NewGuid();

    private IUserManagementService _users = null!;
    private IAdministratorRoleLock _lock = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteSystemUserSkill _sut = null!;

    private static SkillExecutionContext Ctx(Guid callerId) => new()
    {
        UserId = callerId,
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    [SetUp]
    public void SetUp()
    {
        _users = Substitute.For<IUserManagementService>();
        _users.FindUserByIdAsync(TargetId.ToString())
            .Returns(new AppUser { Id = TargetId.ToString(), FirstName = "Anna", LastName = "Muster", Email = "anna@example.test" });
        _users.DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns((true, "done"));
        _lock = Substitute.For<IAdministratorRoleLock>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<(bool Success, string Message)>>>())
            .Returns(call => call.ArgAt<Func<Task<(bool Success, string Message)>>>(0)());
        _sut = new DeleteSystemUserSkill(_users, _lock, _unitOfWork);
    }

    [Test]
    public async Task Deleting_TakesTheLockInsideTheTransaction_BeforeTheAccountIsDeactivated()
    {
        var callerId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        var result = await _sut.ExecuteAsync(
            Ctx(callerId), new Dictionary<string, object> { ["userId"] = TargetId.ToString() }, cts.Token);

        result.Success.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<(bool Success, string Message)>>>());
            _lock.AcquireAsync(cts.Token);
            _users.DeactivateUserAsync(TargetId, callerId.ToString());
        });
    }

    [Test]
    public async Task TheLockCannotBeTaken_NothingIsDeactivated()
    {
        _lock.AcquireAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("lock unavailable")));

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.ExecuteAsync(
            Ctx(Guid.NewGuid()), new Dictionary<string, object> { ["userId"] = TargetId.ToString() }));

        await _users.DidNotReceive().DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task TheLastActiveAdministrator_IsRefusedAsAnError()
    {
        _users.DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((false, LastAdministratorRule.DeactivationRefusalMessage));

        var result = await _sut.ExecuteAsync(
            Ctx(Guid.NewGuid()), new Dictionary<string, object> { ["userId"] = TargetId.ToString() });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(LastAdministratorRule.DeactivationRefusalMessage);
    }

    [Test]
    public async Task UnknownAccount_IsRefusedWithoutTakingTheLock()
    {
        _users.FindUserByIdAsync(Arg.Any<string>()).Returns((AppUser?)null);

        var result = await _sut.ExecuteAsync(
            Ctx(Guid.NewGuid()), new Dictionary<string, object> { ["userId"] = Guid.NewGuid().ToString() });

        result.Success.ShouldBeFalse();
        await _lock.DidNotReceive().AcquireAsync(Arg.Any<CancellationToken>());
    }
}
