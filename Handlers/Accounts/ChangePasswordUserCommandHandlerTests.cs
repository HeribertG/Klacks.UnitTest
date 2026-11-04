// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ChangePasswordUserCommandHandler: a failed change leaves the transaction lambda normally (so the
/// failed-access count is committed) and is only turned into an exception afterwards; the confirmation mail is sent
/// only after the transaction committed and not at all when the transaction fails; it goes to the account's stored
/// address, not to the e-mail in the request body; the caller id is passed through unchanged.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class ChangePasswordUserCommandHandlerTests
{
    private const string CallerId = "11111111-aaaa-bbbb-cccc-000000000001";
    private const string StoredEmail = "caller@example.com";
    private const string BodyEmail = "colleague@example.com";
    private const string MailSent = "true";
    private const string CommitFailure = "commit failed";
    private const string MailFailureText = "Email configuration not available";

    private IAccountPasswordService _passwordService = null!;
    private IAccountNotificationService _notificationService = null!;
    private IAccountAuthenticationService _authenticationService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ChangePasswordUserCommandHandler _sut = null!;
    private AuthenticatedResult? _transactionOutcome;
    private int? _mailCallsWhenTransactionCompleted;

    [SetUp]
    public void SetUp()
    {
        _passwordService = Substitute.For<IAccountPasswordService>();
        _notificationService = Substitute.For<IAccountNotificationService>();
        _authenticationService = Substitute.For<IAccountAuthenticationService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _transactionOutcome = null;
        _mailCallsWhenTransactionCompleted = null;
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<AuthenticatedResult>>>())
            .Returns(async call =>
            {
                var outcome = await call.ArgAt<Func<Task<AuthenticatedResult>>>(0)();
                _transactionOutcome = outcome;
                _mailCallsWhenTransactionCompleted = _notificationService.ReceivedCalls().Count();
                return outcome;
            });

        _sut = new ChangePasswordUserCommandHandler(
            _passwordService,
            _notificationService,
            _authenticationService,
            _unitOfWork,
            Substitute.For<ILogger<ChangePasswordUserCommandHandler>>());
    }

    [Test]
    public async Task Handle_WhenTheChangeFails_CompletesTheTransactionAndThrowsAfterwards()
    {
        var command = CreateCommand();
        _passwordService.ChangePasswordAsync(CallerId, command.ChangePassword)
            .Returns(new AuthenticatedResult { Success = false });

        await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(command, CancellationToken.None));

        _transactionOutcome.ShouldNotBeNull();
        _transactionOutcome!.Success.ShouldBeFalse();
        await _notificationService.DidNotReceive().SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WhenTheChangeSucceeds_MailsTheStoredAddressAndNotTheBodyEmail()
    {
        var command = CreateCommand();
        ArrangeSuccessfulChange(command);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.MailSuccess.ShouldBeTrue();
        result.RefreshToken.ShouldBe("refresh");
        await _notificationService.Received(1).SendEmailAsync(command.ChangePassword.Title, StoredEmail, Arg.Any<string>());
        await _notificationService.DidNotReceive().SendEmailAsync(Arg.Any<string>(), BodyEmail, Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WhenTheChangeSucceeds_SendsTheMailOnlyAfterTheTransactionCompleted()
    {
        var command = CreateCommand();
        ArrangeSuccessfulChange(command);

        await _sut.Handle(command, CancellationToken.None);

        _mailCallsWhenTransactionCompleted.ShouldBe(0);
        await _notificationService.Received(1).SendEmailAsync(command.ChangePassword.Title, StoredEmail, Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WhenTheTransactionFailsAfterTheChange_SendsNoMail()
    {
        var command = CreateCommand();
        ArrangeSuccessfulChange(command);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<AuthenticatedResult>>>())
            .Returns<Task<AuthenticatedResult>>(async call =>
            {
                await call.ArgAt<Func<Task<AuthenticatedResult>>>(0)();
                throw new InvalidOperationException(CommitFailure);
            });

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.Handle(command, CancellationToken.None));

        await _notificationService.DidNotReceive().SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WhenTheMailCannotBeSent_KeepsTheChangedSessionAndReportsTheMailFailure()
    {
        var command = CreateCommand();
        ArrangeSuccessfulChange(command);
        _notificationService.SendEmailAsync(command.ChangePassword.Title, StoredEmail, Arg.Any<string>()).Returns(MailFailureText);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.Token.ShouldBe("access");
        result.RefreshToken.ShouldBe("refresh");
        result.MailSuccess.ShouldBeFalse();
        _authenticationService.Received(1).SetModelError(result, Arg.Any<string>(), MailFailureText);
    }

    private void ArrangeSuccessfulChange(ChangePasswordUserCommand command)
    {
        _passwordService.ChangePasswordAsync(CallerId, command.ChangePassword)
            .Returns(new AuthenticatedResult { Success = true, Email = StoredEmail, Token = "access", RefreshToken = "refresh" });
        _notificationService.SendEmailAsync(command.ChangePassword.Title, StoredEmail, Arg.Any<string>()).Returns(MailSent);
    }

    private static ChangePasswordUserCommand CreateCommand() => new(
        CallerId,
        new ChangePasswordResource
        {
            Email = BodyEmail,
            OldPassword = "OldPassword123!",
            Password = "NewPassword123!",
            Title = "Password changed",
            Message = "Your {appName} password was changed."
        });
}
