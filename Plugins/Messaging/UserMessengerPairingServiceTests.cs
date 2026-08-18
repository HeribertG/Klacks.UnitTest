// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the code flow that gives UserMessengerContact its first writer. What is load-bearing here:
/// a redeemed code creates the contact for the user the code was issued for and for nobody else, a
/// spent or expired code is refused with a distinguishable reason, and the very first channel of a
/// user becomes the preferred one - that is the channel a night-time wake-up call is sent to.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
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
public class UserMessengerPairingServiceTests
{
    private const string UserId = "3f9a2b10-77c5-4de1-9a02-5b1c8e4d6a33";
    private const string OtherUserId = "b1d4e7c2-3a56-4f80-8c19-2d7e5a0b4c66";
    private const string ChatId = "884411223";
    private const string Code = "K7M4PQRS";
    private const string AdminId = "a1e2c3d4-9b7f-4e60-8a12-6c3d9f0b1e77";

    private IUserMessengerPairingCodeStore _codeStore = null!;
    private IUserMessengerContactRepository _contactRepository = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private UserMessengerPairingService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _codeStore = Substitute.For<IUserMessengerPairingCodeStore>();
        _contactRepository = Substitute.For<IUserMessengerContactRepository>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();

        _sut = new UserMessengerPairingService(
            _codeStore,
            _contactRepository,
            _unitOfWork,
            NullLogger<UserMessengerPairingService>.Instance);
    }

    [Test]
    public async Task RedeemAsync_ValidCode_CreatesTheContactForTheIssuingUser()
    {
        GivePeek(UserMessengerPairingLookup.Success(ValidCode()));
        GiveExistingChannels();

        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        result.ShouldBe(OnboardingRedeemResult.Success);
        await _contactRepository.Received(1).AddAsync(
            Arg.Is<UserMessengerContact>(c =>
                c.UserId == UserId
                && c.Type == MessengerType.Telegram
                && c.Value == ChatId
                && !c.IsDeleted),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RedeemAsync_ValidCode_BurnsTheCode()
    {
        GivePeek(UserMessengerPairingLookup.Success(ValidCode()));
        GiveExistingChannels();

        await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        await _codeStore.Received(1).MarkUsedAsync(Code, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_FirstChannelOfTheUser_BecomesThePreferredOne()
    {
        GivePeek(UserMessengerPairingLookup.Success(ValidCode()));
        GiveExistingChannels();

        await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        await _contactRepository.Received(1).AddAsync(
            Arg.Is<UserMessengerContact>(c => c.IsPreferred),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_SecondChannelOfTheUser_IsNotPreferredAutomatically()
    {
        GivePeek(UserMessengerPairingLookup.Success(ValidCode()));
        GiveExistingChannels(new UserMessengerContact
        {
            UserId = UserId,
            Type = MessengerType.Slack,
            Value = "U4PLANNER",
            IsPreferred = true
        });

        await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        await _contactRepository.Received(1).AddAsync(
            Arg.Is<UserMessengerContact>(c => !c.IsPreferred),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_ExpiredCode_IsRefusedAndWritesNothing()
    {
        GivePeek(UserMessengerPairingLookup.Expired);

        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        result.ShouldBe(OnboardingRedeemResult.TokenExpired);
        await _contactRepository.DidNotReceive().AddAsync(Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
        await _codeStore.DidNotReceive().MarkUsedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_AlreadyUsedCode_IsRefusedAndWritesNothing()
    {
        GivePeek(UserMessengerPairingLookup.AlreadyUsed);

        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        result.ShouldBe(OnboardingRedeemResult.TokenAlreadyUsed);
        await _contactRepository.DidNotReceive().AddAsync(Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_UnknownCode_IsRefusedAndWritesNothing()
    {
        GivePeek(UserMessengerPairingLookup.NotFound);

        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        result.ShouldBe(OnboardingRedeemResult.TokenNotFound);
        await _contactRepository.DidNotReceive().AddAsync(Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_ChannelAlreadyLinkedToTheSameUser_IsNotAddedTwice()
    {
        GivePeek(UserMessengerPairingLookup.Success(ValidCode()));
        GiveLinkedContact(UserId);

        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        result.ShouldBe(OnboardingRedeemResult.Success);
        await _contactRepository.DidNotReceive().AddAsync(Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
        await _codeStore.Received(1).MarkUsedAsync(Code, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A chat id that already belongs to somebody else must not change owner: whoever holds it would
    /// otherwise inherit that account's wake-up calls.
    /// </summary>
    [Test]
    public async Task RedeemAsync_ChannelLinkedToAnotherUser_IsRefusedAndTheCodeSurvives()
    {
        GivePeek(UserMessengerPairingLookup.Success(ValidCode()));
        GiveLinkedContact(OtherUserId);

        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, ChatId);

        result.ShouldBe(OnboardingRedeemResult.ContactAlreadyLinked);
        await _contactRepository.DidNotReceive().AddAsync(Arg.Any<UserMessengerContact>(), Arg.Any<CancellationToken>());
        await _codeStore.DidNotReceive().MarkUsedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RedeemAsync_BlankSender_IsRefusedBeforeTheStoreIsTouched()
    {
        var result = await _sut.RedeemAsync(Code, MessengerType.Telegram, "   ");

        result.ShouldBe(OnboardingRedeemResult.TokenNotFound);
        await _codeStore.DidNotReceive().PeekAsync(
            Arg.Any<string>(), Arg.Any<MessengerType>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IssueCodeAsync_AlwaysIssuesForTheGivenUserOnTelegram()
    {
        _codeStore
            .IssueAsync(UserId, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(ValidCode());

        var issued = await _sut.IssueCodeAsync(UserId);

        issued.UserId.ShouldBe(UserId);
        await _codeStore.Received(1).IssueAsync(UserId, MessengerType.Telegram, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// IssueAdminInviteAsync is a thin pass-through to the store: the pairing invariants themselves
    /// (superseding, lifetime, who a code belongs to) live in the store and are covered there. What
    /// this service must get right is forwarding the exact userId/adminId pair and handing back
    /// whatever the store issued.
    /// </summary>
    [Test]
    public async Task IssueAdminInviteAsync_DelegatesToTheCodeStoreWithTheGivenAdminAndReturnsItsResult()
    {
        var invite = new UserMessengerPairingCode(
            Code,
            UserId,
            MessengerType.Telegram,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(UserMessengerPairingConstants.AdminInviteCodeLifetimeHours),
            UsedAt: null,
            IssuedByAdminId: AdminId);
        _codeStore
            .IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId, Arg.Any<CancellationToken>())
            .Returns(invite);

        var issued = await _sut.IssueAdminInviteAsync(UserId, AdminId);

        issued.ShouldBe(invite);
        await _codeStore.Received(1).IssueAdminInviteAsync(
            UserId, MessengerType.Telegram, AdminId, Arg.Any<CancellationToken>());
    }

    private static UserMessengerPairingCode ValidCode() =>
        new(Code, UserId, MessengerType.Telegram, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15), UsedAt: null);

    private void GivePeek(UserMessengerPairingLookup lookup)
    {
        _codeStore
            .PeekAsync(Code, MessengerType.Telegram, Arg.Any<CancellationToken>())
            .Returns(lookup);
    }

    private void GiveLinkedContact(string userId)
    {
        _contactRepository
            .GetByTypeAndValueAsync(MessengerType.Telegram, ChatId, Arg.Any<CancellationToken>())
            .Returns(new UserMessengerContact { UserId = userId, Type = MessengerType.Telegram, Value = ChatId });
    }

    private void GiveExistingChannels(params UserMessengerContact[] channels)
    {
        _contactRepository
            .GetByUserIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<UserMessengerContact>)channels);
    }
}
