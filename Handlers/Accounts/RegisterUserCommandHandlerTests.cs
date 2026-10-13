// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class RegisterUserCommandHandlerTests
{
    private IAccountRegistrationService _registrationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private RegisterUserCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _registrationService = Substitute.For<IAccountRegistrationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<AuthenticatedResult>>>())
            .Returns(ci => ci.ArgAt<Func<Task<AuthenticatedResult>>>(0)());

        _sut = new RegisterUserCommandHandler(
            _registrationService,
            new AuthMapper(),
            _unitOfWork,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<RegisterUserCommandHandler>>());
    }

    private static RegisterUserCommand Cmd(string email = "new.user@example.com") => new(new RegistrationResource
    {
        Email = email,
        FirstName = "New",
        LastName = "User",
        Password = "password1!",
        UserName = "nuser",
    });

    [Test]
    public async Task Handle_Success_ReturnsTheAuthenticatedResult()
    {
        var success = new AuthenticatedResult { Success = true };
        _registrationService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(success);

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result.ShouldBeSameAs(success);
    }

    [Test]
    public async Task Handle_InvalidEmail_RejectsWithInvalidEmailReasonBeforeRegistering()
    {
        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(email: "no-at-sign"), CancellationToken.None));

        ex.Reasons.ShouldBe([AccountRegistrationErrorCodes.InvalidEmail]);
        await _registrationService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_NullPassword_IsPassedAsEmptySoIdentityAnswersInsteadOfAServerError()
    {
        var command = new RegisterUserCommand(new RegistrationResource
        {
            Email = "new.user@example.com",
            FirstName = "New",
            LastName = "User",
            Password = null!,
            UserName = "nuser",
        });
        _registrationService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(new AuthenticatedResult { Success = true });

        await _sut.Handle(command, CancellationToken.None);

        await _registrationService.Received(1).RegisterUserAsync(Arg.Any<AppUser>(), string.Empty);
    }

    [Test]
    public async Task Handle_RefusalWithoutModelState_RejectsWithTheResultMessage()
    {
        _registrationService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(new AuthenticatedResult { Success = false, Message = "store unavailable" });

        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Reasons.ShouldBeEmpty();
        ex.Message.ShouldBe("store unavailable");
    }

    [Test]
    public async Task Handle_IdentityRefusal_RejectsWithTheIdentityCodesInsteadOfAConflict()
    {
        var describer = new IdentityErrorDescriber();
        var refused = new AuthenticatedResult { Success = false, ModelState = new ModelStateDictionary() };
        var duplicate = describer.DuplicateEmail("new.user@example.com");
        var tooShort = describer.PasswordTooShort(PasswordPolicyDefaults.RequiredLength);
        refused.ModelState.TryAddModelError(duplicate.Code, duplicate.Description);
        refused.ModelState.TryAddModelError(tooShort.Code, tooShort.Description);
        _registrationService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(refused);

        var ex = await Should.ThrowAsync<AccountRegistrationRejectedException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Reasons.ShouldBe(
            [AccountRegistrationErrorCodes.DuplicateEmail, AccountRegistrationErrorCodes.PasswordTooShort],
            ignoreOrder: true);
        ex.Message.ShouldContain(duplicate.Description);
    }
}
