// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class TelegramOnboardingRedemptionServiceTests
{
    private ITelegramOnboardingTokenRepository _tokenRepo = null!;
    private IMessengerContactRepository _contactRepo = null!;
    private IPluginUnitOfWork _unitOfWork = null!;
    private TelegramOnboardingRedemptionService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _tokenRepo = Substitute.For<ITelegramOnboardingTokenRepository>();
        _contactRepo = Substitute.For<IMessengerContactRepository>();
        _unitOfWork = Substitute.For<IPluginUnitOfWork>();
        var logger = Substitute.For<ILogger<TelegramOnboardingRedemptionService>>();
        _sut = new TelegramOnboardingRedemptionService(_tokenRepo, _contactRepo, _unitOfWork, logger);
    }

    [Test]
    public async Task RedeemAsync_Returns_TokenNotFound_When_Missing()
    {
        _tokenRepo.GetByTokenAsync("abc", Arg.Any<CancellationToken>())
            .Returns((TelegramOnboardingToken?)null);

        var result = await _sut.RedeemAsync("abc", "123");

        result.ShouldBe(OnboardingRedeemResult.TokenNotFound);
    }

    [Test]
    public async Task RedeemAsync_Returns_TokenNotFound_When_Token_Or_ChatId_Empty()
    {
        (await _sut.RedeemAsync(string.Empty, "123")).ShouldBe(OnboardingRedeemResult.TokenNotFound);
        (await _sut.RedeemAsync("abc", string.Empty)).ShouldBe(OnboardingRedeemResult.TokenNotFound);
    }

    [Test]
    public async Task RedeemAsync_Returns_TokenExpired_When_Past_ExpiresAt()
    {
        _tokenRepo.GetByTokenAsync("abc", Arg.Any<CancellationToken>())
            .Returns(new TelegramOnboardingToken
            {
                Token = "abc",
                ClientId = Guid.NewGuid(),
                ExpiresAt = DateTime.UtcNow.AddDays(-1),
            });

        var result = await _sut.RedeemAsync("abc", "123");

        result.ShouldBe(OnboardingRedeemResult.TokenExpired);
    }

    [Test]
    public async Task RedeemAsync_Returns_TokenAlreadyUsed_When_UsedAt_Set()
    {
        _tokenRepo.GetByTokenAsync("abc", Arg.Any<CancellationToken>())
            .Returns(new TelegramOnboardingToken
            {
                Token = "abc",
                ClientId = Guid.NewGuid(),
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                UsedAt = DateTime.UtcNow.AddMinutes(-5),
            });

        var result = await _sut.RedeemAsync("abc", "123");

        result.ShouldBe(OnboardingRedeemResult.TokenAlreadyUsed);
    }

    [Test]
    public async Task RedeemAsync_Creates_MessengerContact_And_Marks_Token_Used()
    {
        var clientId = Guid.NewGuid();
        var token = new TelegramOnboardingToken
        {
            Token = "abc",
            ClientId = clientId,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        _tokenRepo.GetByTokenAsync("abc", Arg.Any<CancellationToken>()).Returns(token);

        var result = await _sut.RedeemAsync("abc", "999111");

        result.ShouldBe(OnboardingRedeemResult.Success);
        await _contactRepo.Received(1).AddAsync(
            Arg.Is<MessengerContact>(c =>
                c.ClientId == clientId
                && c.Type == MessengerType.Telegram
                && c.Value == "999111"),
            Arg.Any<CancellationToken>());
        token.UsedAt.ShouldNotBeNull();
        token.RedeemedChatId.ShouldBe("999111");
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
