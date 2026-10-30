// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Identity;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class CompleteOwnAdminSetupCommandHandlerTests
{
    private const string GeneratedUserName = "nadmin";
    private const string ClientUserName = "client-chosen-name";

    private IAdminSetupGateService _gateService = null!;
    private IUserManagementService _userManagementService = null!;
    private IAdministratorRoleLock _administratorRoleLock = null!;
    private IUsernameGeneratorService _usernameGeneratorService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CompleteOwnAdminSetupCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _gateService = Substitute.For<IAdminSetupGateService>();
        _userManagementService = Substitute.For<IUserManagementService>();
        _administratorRoleLock = Substitute.For<IAdministratorRoleLock>();
        _usernameGeneratorService = Substitute.For<IUsernameGeneratorService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.ArgAt<Func<Task<bool>>>(0)());
        _usernameGeneratorService.GenerateUniqueUsernameAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(GeneratedUserName);

        _sut = new CompleteOwnAdminSetupCommandHandler(
            _gateService,
            _userManagementService,
            _administratorRoleLock,
            _usernameGeneratorService,
            new AuthMapper(),
            _unitOfWork,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<CompleteOwnAdminSetupCommandHandler>>());
    }

    private static CompleteOwnAdminSetupCommand Cmd(string email = "new-admin@example.com", string firstName = "New") =>
        new(new RegistrationResource
        {
            Email = email,
            FirstName = firstName,
            LastName = "Admin",
            Password = "Password1!",
            UserName = ClientUserName,
        });

    private void GateActive()
    {
        _gateService.IsExemptAsync().Returns(false);
        _gateService.IsGateActiveAsync().Returns(true);
    }

    private void AllStepsSucceed()
    {
        _userManagementService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((true, (IdentityResult?)IdentityResult.Success));
        _userManagementService.ChangeUserRoleAsync(Arg.Any<string>(), Roles.Admin, true)
            .Returns((true, string.Empty));
        _userManagementService.DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((true, string.Empty));
    }

    [Test]
    public async Task Handle_ExemptEnvironment_ThrowsWithEnvironmentMessage()
    {
        _gateService.IsExemptAsync().Returns(true);

        var ex = await Should.ThrowAsync<ConflictException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Message.ShouldBe(AdminSetupMessages.NotRequiredInEnvironment);
        await _gateService.DidNotReceive().IsGateActiveAsync();
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_NotExemptButGateInactive_ThrowsAlreadyCompleted()
    {
        _gateService.IsExemptAsync().Returns(false);
        _gateService.IsGateActiveAsync().Returns(false);

        var ex = await Should.ThrowAsync<ConflictException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Message.ShouldBe(AdminSetupMessages.AlreadyCompleted);
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_GateActive_RegistersNewAdminAndDeactivatesSeedAdmin()
    {
        GateActive();
        AllStepsSucceed();

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result.ShouldBeTrue();
        await _userManagementService.Received(1).RegisterUserAsync(Arg.Any<AppUser>(), "Password1!");
        await _userManagementService.Received(1).DeactivateUserAsync(
            Guid.Parse(SystemAccounts.SeedAdminUserId), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_GateActive_TakesTheAdministratorLockFirstInsideTheTransaction()
    {
        GateActive();
        AllStepsSucceed();
        using var cts = new CancellationTokenSource();

        await _sut.Handle(Cmd(), cts.Token);

        Received.InOrder(() =>
        {
            _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
            _administratorRoleLock.AcquireAsync(cts.Token);
            _gateService.IsExemptAsync();
            _gateService.IsGateActiveAsync();
            _userManagementService.ChangeUserRoleAsync(Arg.Any<string>(), Roles.Admin, true);
            _userManagementService.DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
        });
    }

    [Test]
    public async Task Handle_TheLockCannotBeTaken_TheSeedAdminIsNotDeactivated()
    {
        GateActive();
        AllStepsSucceed();
        _administratorRoleLock.AcquireAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("lock unavailable")));

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.Handle(Cmd(), CancellationToken.None));

        await _userManagementService.DidNotReceive().DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_GateActive_UsesTheGeneratedUserNameAndIgnoresTheClientOne()
    {
        GateActive();
        AllStepsSucceed();

        await _sut.Handle(Cmd(), CancellationToken.None);

        await _usernameGeneratorService.Received(1).GenerateUniqueUsernameAsync("New", "Admin");
        await _userManagementService.Received(1).RegisterUserAsync(
            Arg.Is<AppUser>(user => user.UserName == GeneratedUserName), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_EmailWithSurroundingWhitespace_RegistersTheTrimmedAddress()
    {
        GateActive();
        AllStepsSucceed();

        await _sut.Handle(Cmd(email: "  new-admin@example.com "), CancellationToken.None);

        await _userManagementService.Received(1).RegisterUserAsync(
            Arg.Is<AppUser>(user => user.Email == "new-admin@example.com"), Arg.Any<string>());
    }

    [TestCase("not-an-address")]
    [TestCase("Name <new-admin@example.com>")]
    [TestCase("")]
    public async Task Handle_InvalidEmail_RejectsWithInvalidEmailReasonBeforeRegistering(string email)
    {
        GateActive();

        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(email: email), CancellationToken.None));

        ex.Reasons.ShouldBe([AccountRegistrationErrorCodes.InvalidEmail]);
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_MissingFirstName_RejectsAsInvalidRequest()
    {
        GateActive();

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            async () => await _sut.Handle(Cmd(firstName: "  "), CancellationToken.None));

        ex.ShouldNotBeOfType<AccountRegistrationRejectedException>();
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_IdentityRejectsThePassword_RejectsWithIdentityCodesAndDoesNotDeactivate()
    {
        GateActive();
        var describer = new IdentityErrorDescriber();
        _userManagementService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((false, (IdentityResult?)IdentityResult.Failed(
                describer.PasswordTooShort(PasswordPolicyDefaults.RequiredLength),
                describer.PasswordRequiresDigit())));

        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Reasons.ShouldBe([AccountRegistrationErrorCodes.PasswordTooShort, AccountRegistrationErrorCodes.PasswordRequiresDigit]);
        await _userManagementService.DidNotReceive().ChangeUserRoleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _userManagementService.DidNotReceive().DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [TestCase(AccountRegistrationErrorCodes.DuplicateEmail)]
    [TestCase(AccountRegistrationErrorCodes.InvalidUserName)]
    public async Task Handle_RegistrationRefused_PassesTheReasonThrough(string code)
    {
        GateActive();
        _userManagementService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((false, (IdentityResult?)IdentityResult.Failed(new IdentityError { Code = code, Description = code })));

        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Reasons.ShouldBe([code]);
    }

    [Test]
    public async Task Handle_RegistrationRefusedWithoutResult_RejectsWithTheGenericMessage()
    {
        GateActive();
        _userManagementService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((false, (IdentityResult?)null));

        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Reasons.ShouldBeEmpty();
        ex.Message.ShouldBe(AccountRegistrationMessages.Rejected);
    }

    [Test]
    public async Task Handle_MissingLastName_RejectsAsInvalidRequest()
    {
        GateActive();
        var command = new CompleteOwnAdminSetupCommand(new RegistrationResource
        {
            Email = "new-admin@example.com",
            FirstName = "New",
            LastName = string.Empty,
            Password = "Password1!",
        });

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        ex.Message.ShouldBe(AccountRegistrationMessages.LastNameRequired);
    }

    [Test]
    public async Task Handle_MissingRegistration_RejectsAsInvalidRequest()
    {
        GateActive();

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            async () => await _sut.Handle(new CompleteOwnAdminSetupCommand(null!), CancellationToken.None));

        ex.Message.ShouldBe(AccountRegistrationMessages.RegistrationRequired);
    }

    [Test]
    public async Task Handle_RoleCannotBeGranted_FailsAsServerErrorNotAsConflict()
    {
        GateActive();
        AllStepsSucceed();
        _userManagementService.ChangeUserRoleAsync(Arg.Any<string>(), Roles.Admin, true)
            .Returns((false, "role store unavailable"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Message.ShouldBe(AdminSetupMessages.AdminRoleNotGranted);
        await _userManagementService.DidNotReceive().DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_SeedAdminCannotBeDeactivated_FailsAsServerErrorNotAsConflict()
    {
        GateActive();
        AllStepsSucceed();
        _userManagementService.DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((false, "update failed"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Message.ShouldBe(AdminSetupMessages.SeedAdminNotDeactivated);
    }
}
