// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the admin-initiated Telegram invite flow end to end at the service boundary. What is
/// load-bearing here: every refusal path (unknown user, already linked, no email, bot unresolved)
/// stops before touching state further downstream than it has to, the deliberate exception is that a
/// pairing code is already issued by the time the email send is attempted - so a failed send still
/// leaks a live code - and the success path both issues the code for the right admin/target pair and
/// emails the resulting deep-link to the target's own address.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class UserInviteSendServiceTests
{
    private const string UserId = "3f9a2b10-77c5-4de1-9a02-5b1c8e4d6a33";
    private const string AdminId = "a1e2c3d4-9b7f-4e60-8a12-6c3d9f0b1e77";
    private const string Email = "jane@example.com";
    private const string BotConfig = "{\"BotToken\":\"test-token\"}";
    private const string BotUsername = "klacks_bot";
    private const string Code = "K7M4PQRS";

    private IAppUserDirectoryReader _userDirectory = null!;
    private IUserMessengerContactRepository _contactRepository = null!;
    private IUserMessengerPairingService _pairingService = null!;
    private IPluginEmailSender _emailSender = null!;
    private ITelegramBotMetadataProvider _botMetadataProvider = null!;
    private UserInviteSendService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _userDirectory = Substitute.For<IAppUserDirectoryReader>();
        _contactRepository = Substitute.For<IUserMessengerContactRepository>();
        _pairingService = Substitute.For<IUserMessengerPairingService>();
        _emailSender = Substitute.For<IPluginEmailSender>();
        _botMetadataProvider = Substitute.For<ITelegramBotMetadataProvider>();

        _sut = new UserInviteSendService(
            _userDirectory,
            _contactRepository,
            _pairingService,
            _emailSender,
            _botMetadataProvider,
            NullLogger<UserInviteSendService>.Instance);
    }

    [Test]
    public async Task SendAsync_TargetUserDoesNotExist_ReturnsUserNotFoundAndTouchesNothingDownstream()
    {
        _userDirectory.GetUserAsync(UserId, Arg.Any<CancellationToken>()).Returns((AppUserDirectoryInfo?)null);

        var result = await _sut.SendAsync(UserId, AdminId, BotConfig);

        result.ShouldBe(UserInviteSendResult.UserNotFound);
        await _contactRepository.DidNotReceive().GetByUserAndTypeAsync(
            Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_TargetAlreadyHasATelegramContact_ReturnsAlreadyLinkedBeforeCheckingEmailOrBot()
    {
        GiveUser(email: Email);
        _contactRepository.GetByUserAndTypeAsync(UserId, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = UserId, Type = MessengerType.Telegram, Value = "12345" });

        var result = await _sut.SendAsync(UserId, AdminId, BotConfig);

        result.ShouldBe(UserInviteSendResult.AlreadyLinked);
        await _botMetadataProvider.DidNotReceive().GetBotUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(null)]
    [TestCase("   ")]
    public async Task SendAsync_TargetHasNoUsableEmail_ReturnsNoEmailWithoutIssuingACode(string? email)
    {
        GiveUser(email: email);

        var result = await _sut.SendAsync(UserId, AdminId, BotConfig);

        result.ShouldBe(UserInviteSendResult.NoEmail);
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task SendAsync_BotUsernameCannotBeResolved_ReturnsSendFailedWithoutIssuingACode(string? botUsername)
    {
        GiveUser(email: Email);
        _botMetadataProvider.GetBotUsernameAsync(BotConfig, Arg.Any<CancellationToken>()).Returns(botUsername);

        var result = await _sut.SendAsync(UserId, AdminId, BotConfig);

        result.ShouldBe(UserInviteSendResult.SendFailed);
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The pairing code is issued before the email is sent, so a send failure still leaves a live
    /// admin-scoped code behind - a deliberate consequence of the ordering, not an oversight, and
    /// worth pinning explicitly so a future reordering does not silently change this.
    /// </summary>
    [Test]
    public async Task SendAsync_EmailDispatchFails_ReturnsSendFailedButTheCodeWasAlreadyIssued()
    {
        GiveUser(email: Email);
        GiveResolvedBot();
        GiveIssuedCode();
        _emailSender.SendEmailAsync(Email, UserInviteConstants.InvitationSubject, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.SendAsync(UserId, AdminId, BotConfig);

        result.ShouldBe(UserInviteSendResult.SendFailed);
        await _pairingService.Received(1).IssueAdminInviteAsync(UserId, AdminId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_EverythingSucceeds_IssuesTheAdminCodeAndEmailsTheDeepLinkToTheTarget()
    {
        GiveUser(email: Email, firstName: "Jane");
        GiveResolvedBot();
        var issued = GiveIssuedCode();
        _emailSender.SendEmailAsync(Email, UserInviteConstants.InvitationSubject, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SendAsync(UserId, AdminId, BotConfig);

        result.ShouldBe(UserInviteSendResult.Success);
        await _pairingService.Received(1).IssueAdminInviteAsync(UserId, AdminId, Arg.Any<CancellationToken>());
        await _emailSender.Received(1).SendEmailAsync(
            Email,
            UserInviteConstants.InvitationSubject,
            Arg.Is<string>(body => body.Contains($"https://t.me/{BotUsername}?start={issued.Code}")),
            Arg.Any<CancellationToken>());
    }

    private void GiveUser(string? email, string? firstName = "Jane") =>
        _userDirectory.GetUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AppUserDirectoryInfo(UserId, firstName, "Doe", email));

    private void GiveResolvedBot() =>
        _botMetadataProvider.GetBotUsernameAsync(BotConfig, Arg.Any<CancellationToken>()).Returns(BotUsername);

    private UserMessengerPairingCode GiveIssuedCode()
    {
        var issued = new UserMessengerPairingCode(
            Code,
            UserId,
            MessengerType.Telegram,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(UserMessengerPairingConstants.AdminInviteCodeLifetimeHours),
            UsedAt: null,
            IssuedByAdminId: AdminId);
        _pairingService.IssueAdminInviteAsync(UserId, AdminId, Arg.Any<CancellationToken>()).Returns(issued);
        return issued;
    }
}
