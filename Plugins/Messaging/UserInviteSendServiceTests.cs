// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the admin-initiated, provider-agnostic messenger invite flow end to end at the service
/// boundary. What is load-bearing here: the refusal paths that precede any provider interaction
/// (unknown user, already linked, no email) stop before touching state further downstream than they
/// have to. Building pairing instructions needs the issued code itself (e.g. Telegram's deep link and
/// Slack's "send it this code" text both embed it), so - unlike the old Telegram-only bot-username
/// check, which ran before code issuance - the pairing code is now always issued before instructions
/// are built: a provider failure there leaks a live code exactly like an email-send failure does. The
/// success path issues the code for the right admin/target pair and emails the resulting instructions
/// to the target's own address.
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
    private const string ProviderType = MessagingConstants.ProviderTelegram;
    private const string BotConfig = "{\"BotToken\":\"test-token\"}";
    private const string Instructions = "Open this link on your phone and press START in Telegram:\nhttps://t.me/klacks_bot?start=K7M4PQRS";
    private const string Code = "K7M4PQRS";

    private IAppUserDirectoryReader _userDirectory = null!;
    private IUserMessengerContactRepository _contactRepository = null!;
    private IUserMessengerPairingService _pairingService = null!;
    private IPluginEmailSender _emailSender = null!;
    private IMessagingProviderAdapterFactory _adapterFactory = null!;
    private IMessagingProviderAdapter _providerAdapter = null!;
    private UserInviteSendService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _userDirectory = Substitute.For<IAppUserDirectoryReader>();
        _contactRepository = Substitute.For<IUserMessengerContactRepository>();
        _pairingService = Substitute.For<IUserMessengerPairingService>();
        _emailSender = Substitute.For<IPluginEmailSender>();
        _adapterFactory = Substitute.For<IMessagingProviderAdapterFactory>();
        _providerAdapter = Substitute.For<IMessagingProviderAdapter, IPairingInstructionsProvider>();
        _adapterFactory.Create(ProviderType).Returns(_providerAdapter);

        _sut = new UserInviteSendService(
            _userDirectory,
            _contactRepository,
            _pairingService,
            _emailSender,
            _adapterFactory,
            NullLogger<UserInviteSendService>.Instance);
    }

    [Test]
    public async Task SendAsync_TargetUserDoesNotExist_ReturnsUserNotFoundAndTouchesNothingDownstream()
    {
        _userDirectory.GetUserAsync(UserId, Arg.Any<CancellationToken>()).Returns((AppUserDirectoryInfo?)null);

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.UserNotFound);
        await _contactRepository.DidNotReceive().GetByUserAndTypeAsync(
            Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_TargetAlreadyHasATelegramContact_ReturnsAlreadyLinkedBeforeCheckingEmailOrProvider()
    {
        GiveUser(email: Email);
        _contactRepository.GetByUserAndTypeAsync(UserId, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = UserId, Type = MessengerType.Telegram, Value = "12345" });

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.AlreadyLinked);
        _adapterFactory.DidNotReceive().Create(Arg.Any<string>());
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase(null)]
    [TestCase("   ")]
    public async Task SendAsync_TargetHasNoUsableEmail_ReturnsNoEmailWithoutIssuingACode(string? email)
    {
        GiveUser(email: email);

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.NoEmail);
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_ProviderTypeDoesNotMapToAMessengerType_ReturnsSendFailedWithoutTouchingAnythingDownstream()
    {
        GiveUser(email: Email);

        var result = await _sut.SendAsync(UserId, AdminId, "NotARealProvider", BotConfig);

        result.ShouldBe(UserInviteSendResult.SendFailed);
        await _contactRepository.DidNotReceive().GetByUserAndTypeAsync(
            Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The adapter the factory resolves for the given provider type might not implement
    /// IPairingInstructionsProvider at all (a provider not implementing that capability cannot be
    /// used here) - refused before a code is issued, since this check runs ahead of
    /// IssueAdminInviteAsync in SendAsync.
    /// </summary>
    [Test]
    public async Task SendAsync_ResolvedAdapterDoesNotSupportPairingInstructions_ReturnsSendFailedWithoutIssuingACode()
    {
        GiveUser(email: Email);
        _adapterFactory.Create(ProviderType).Returns(Substitute.For<IMessagingProviderAdapter>());

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.SendFailed);
        await _pairingService.DidNotReceive().IssueAdminInviteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// PRODUCTION BEHAVIOR CHANGE from the provider-agnostic refactor, pinned deliberately rather than
    /// silently carried over from the old "bot username unresolved" test: building pairing
    /// instructions needs the code itself (Telegram's deep link and Slack's "send it this code" text
    /// both embed it), so IssueAdminInviteAsync now necessarily runs before
    /// BuildPairingInstructionsAsync. The old bot-username check ran before code issuance and left no
    /// code behind on failure; this equivalent new failure (instructions unresolved) now leaks a live
    /// code exactly like the email-dispatch failure below always has.
    /// </summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task SendAsync_PairingInstructionsCannotBeResolved_ReturnsSendFailedButTheCodeWasAlreadyIssued(string? instructions)
    {
        GiveUser(email: Email);
        GiveIssuedCode();
        GivePairingInstructions(instructions);

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.SendFailed);
        await _pairingService.Received(1).IssueAdminInviteAsync(UserId, AdminId, MessengerType.Telegram, Arg.Any<CancellationToken>());
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
        GiveIssuedCode();
        GivePairingInstructions(Instructions);
        _emailSender.SendEmailAsync(Email, UserInviteConstants.InvitationSubject, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.SendFailed);
        await _pairingService.Received(1).IssueAdminInviteAsync(UserId, AdminId, MessengerType.Telegram, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendAsync_EverythingSucceeds_IssuesTheAdminCodeAndEmailsTheProviderInstructionsToTheTarget()
    {
        GiveUser(email: Email, firstName: "Jane");
        GiveIssuedCode();
        GivePairingInstructions(Instructions);
        _emailSender.SendEmailAsync(Email, UserInviteConstants.InvitationSubject, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.SendAsync(UserId, AdminId, ProviderType, BotConfig);

        result.ShouldBe(UserInviteSendResult.Success);
        await _pairingService.Received(1).IssueAdminInviteAsync(UserId, AdminId, MessengerType.Telegram, Arg.Any<CancellationToken>());
        await _emailSender.Received(1).SendEmailAsync(
            Email,
            UserInviteConstants.InvitationSubject,
            Arg.Is<string>(body => body.Contains(Instructions)),
            Arg.Any<CancellationToken>());
    }

    private void GiveUser(string? email, string? firstName = "Jane") =>
        _userDirectory.GetUserAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AppUserDirectoryInfo(UserId, firstName, "Doe", email));

    private void GivePairingInstructions(string? instructions) =>
        ((IPairingInstructionsProvider)_providerAdapter)
            .BuildPairingInstructionsAsync(Code, BotConfig, Arg.Any<CancellationToken>())
            .Returns(instructions);

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
        _pairingService.IssueAdminInviteAsync(UserId, AdminId, MessengerType.Telegram, Arg.Any<CancellationToken>()).Returns(issued);
        return issued;
    }
}
