// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests the settings-row backed pairing code store. Single use and a distinguishable expiry are the
/// point of the whole store: a user who is late has to be told 'expired' rather than 'unknown', or
/// they cannot tell a mistyped code from one that simply ran out.
/// </summary>
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class SettingsUserMessengerPairingCodeStoreTests
{
    private const string UserId = "3f9a2b10-77c5-4de1-9a02-5b1c8e4d6a33";
    private const string OtherUserId = "b1d4e7c2-3a56-4f80-8c19-2d7e5a0b4c66";
    private const string AdminId = "a1e2c3d4-9b7f-4e60-8a12-6c3d9f0b1e77";

    private IPluginSettingsReader _reader = null!;
    private IPluginSettingsWriter _writer = null!;
    private string? _row;
    private SettingsUserMessengerPairingCodeStore _sut = null!;

    [SetUp]
    public void Setup()
    {
        _row = null;

        _reader = Substitute.For<IPluginSettingsReader>();
        _reader
            .GetSettingAsync(UserMessengerPairingConstants.SettingPendingPairingCodes)
            .Returns(_ => _row);

        _writer = Substitute.For<IPluginSettingsWriter>();
        _writer
            .When(w => w.SetSettingAsync(
                UserMessengerPairingConstants.SettingPendingPairingCodes,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()))
            .Do(call => _row = call.ArgAt<string>(1));

        _sut = new SettingsUserMessengerPairingCodeStore(_reader, _writer);
    }

    [Test]
    public async Task IssueAsync_ProducesACodeOfTheDeclaredShapeAndLifetime()
    {
        var issued = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        issued.Code.Length.ShouldBe(UserMessengerPairingConstants.CodeLength);
        issued.Code.ShouldAllBe(c => UserMessengerPairingConstants.CodeAlphabet.Contains(c));
        issued.UserId.ShouldBe(UserId);
        issued.UsedAt.ShouldBeNull();
        issued.ExpiresAt.ShouldBeGreaterThan(DateTime.UtcNow);
        issued.ExpiresAt.ShouldBeLessThanOrEqualTo(
            DateTime.UtcNow.AddMinutes(UserMessengerPairingConstants.CodeLifetimeMinutes + 1));
    }

    [Test]
    public async Task PeekAsync_FreshCode_IsFoundWithItsOwner()
    {
        var issued = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        var lookup = await _sut.PeekAsync(issued.Code, MessengerType.Telegram);

        lookup.Result.ShouldBe(OnboardingRedeemResult.Success);
        lookup.Code!.UserId.ShouldBe(UserId);
    }

    [Test]
    public async Task PeekAsync_AfterMarkUsed_ReportsAlreadyUsed()
    {
        var issued = await _sut.IssueAsync(UserId, MessengerType.Telegram);
        await _sut.MarkUsedAsync(issued.Code);

        var lookup = await _sut.PeekAsync(issued.Code, MessengerType.Telegram);

        lookup.Result.ShouldBe(OnboardingRedeemResult.TokenAlreadyUsed);
        lookup.Code.ShouldBeNull();
    }

    [Test]
    public async Task PeekAsync_ExpiredCode_ReportsExpiredRatherThanUnknown()
    {
        GiveRow(new UserMessengerPairingCode(
            "EXPIRED1",
            UserId,
            MessengerType.Telegram,
            DateTime.UtcNow.AddMinutes(-60),
            DateTime.UtcNow.AddMinutes(-30),
            UsedAt: null));

        var lookup = await _sut.PeekAsync("EXPIRED1", MessengerType.Telegram);

        lookup.Result.ShouldBe(OnboardingRedeemResult.TokenExpired);
    }

    [Test]
    public async Task PeekAsync_UnknownCode_ReportsNotFound()
    {
        await _sut.IssueAsync(UserId, MessengerType.Telegram);

        var lookup = await _sut.PeekAsync("ZZZZZZZZ", MessengerType.Telegram);

        lookup.Result.ShouldBe(OnboardingRedeemResult.TokenNotFound);
    }

    [Test]
    public async Task PeekAsync_CodeOfAnotherMessengerType_IsNotFound()
    {
        var issued = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        var lookup = await _sut.PeekAsync(issued.Code, MessengerType.Slack);

        lookup.Result.ShouldBe(OnboardingRedeemResult.TokenNotFound);
    }

    /// <summary>
    /// A user keeps exactly one code in flight, so a code they asked for and abandoned cannot be
    /// redeemed later by whoever happened to read it off their screen.
    /// </summary>
    [Test]
    public async Task IssueAsync_SecondCodeForTheSameUser_InvalidatesTheFirst()
    {
        var first = await _sut.IssueAsync(UserId, MessengerType.Telegram);
        var second = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        first.Code.ShouldNotBe(second.Code);
        (await _sut.PeekAsync(first.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.TokenNotFound);
        (await _sut.PeekAsync(second.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.Success);
    }

    [Test]
    public async Task IssueAsync_CodeOfAnotherUser_IsLeftAlone()
    {
        var foreign = await _sut.IssueAsync(OtherUserId, MessengerType.Telegram);
        await _sut.IssueAsync(UserId, MessengerType.Telegram);

        (await _sut.PeekAsync(foreign.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.Success);
    }

    [Test]
    public async Task LoadAsync_UnreadableRow_DoesNotLockEverybodyOut()
    {
        _row = "this is not json";

        var issued = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        (await _sut.PeekAsync(issued.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.Success);
    }

    [Test]
    public async Task IssueAdminInviteAsync_ProducesACodeWithTheAdminInviteLifetimeRatherThanTheSelfServiceOne()
    {
        var issued = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        (issued.ExpiresAt - issued.IssuedAt).ShouldBe(
            TimeSpan.FromHours(UserMessengerPairingConstants.AdminInviteCodeLifetimeHours));
        issued.ExpiresAt.ShouldBeGreaterThan(
            DateTime.UtcNow.AddMinutes(UserMessengerPairingConstants.CodeLifetimeMinutes + 1));
    }

    [Test]
    public async Task IssueAdminInviteAsync_RecordsWhichAdminIssuedTheCode()
    {
        var issued = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        issued.IssuedByAdminId.ShouldBe(AdminId);
    }

    /// <summary>
    /// IssuedByAdminId is a new positional-record parameter with a default value; only a round trip
    /// through the JSON row proves it actually survives Serialize/Deserialize rather than silently
    /// resetting to its default once it comes back out of the settings row.
    /// </summary>
    [Test]
    public async Task IssueAdminInviteAsync_IssuedByAdminIdSurvivesTheJsonRoundTrip()
    {
        var invite = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        var lookup = await _sut.PeekAsync(invite.Code, MessengerType.Telegram);

        lookup.Code!.IssuedByAdminId.ShouldBe(AdminId);
    }

    [Test]
    public async Task IssueAsync_SelfService_LeavesIssuedByAdminIdNull()
    {
        var issued = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        issued.IssuedByAdminId.ShouldBeNull();
    }

    [Test]
    public async Task IssueAdminInviteAsync_SupersedesAnExistingUnusedSelfServiceCode()
    {
        var first = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        var invite = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        (await _sut.PeekAsync(first.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.TokenNotFound);
        (await _sut.PeekAsync(invite.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.Success);
    }

    [Test]
    public async Task IssueAdminInviteAsync_SupersedesAnExistingUnusedAdminInviteCode()
    {
        var first = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        var second = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        (await _sut.PeekAsync(first.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.TokenNotFound);
        (await _sut.PeekAsync(second.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.Success);
    }

    /// <summary>
    /// The superseding mechanism is shared and symmetric: an admin-issued code left unused is dropped
    /// just like a self-service one the moment the same user requests any new code for the same type.
    /// </summary>
    [Test]
    public async Task IssueAsync_SupersedesAnExistingUnusedAdminInviteCode()
    {
        var invite = await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, AdminId);

        var second = await _sut.IssueAsync(UserId, MessengerType.Telegram);

        (await _sut.PeekAsync(invite.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.TokenNotFound);
        (await _sut.PeekAsync(second.Code, MessengerType.Telegram)).Result
            .ShouldBe(OnboardingRedeemResult.Success);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task IssueAdminInviteAsync_BlankAdminId_ThrowsArgumentException(string adminId)
    {
        await Should.ThrowAsync<ArgumentException>(
            async () => await _sut.IssueAdminInviteAsync(UserId, MessengerType.Telegram, adminId));
    }

    private void GiveRow(params UserMessengerPairingCode[] records)
    {
        _row = System.Text.Json.JsonSerializer.Serialize(
            records,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
    }
}
