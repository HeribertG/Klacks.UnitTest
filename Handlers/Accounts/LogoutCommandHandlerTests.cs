// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Logout signs out this device only (owner decision 2026-11-05): the presented refresh token is removed, never all
/// tokens of the user; without a token nothing is removed.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class LogoutCommandHandlerTests
{
    private const string DeviceToken = "device-refresh-token";

    private IRefreshTokenService _refreshTokenService = null!;
    private LogoutCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _refreshTokenService = Substitute.For<IRefreshTokenService>();
        _handler = new LogoutCommandHandler(_refreshTokenService);
    }

    [Test]
    public async Task Handle_RemovesOnlyThePresentedToken_NeverAllTokensOfTheUser()
    {
        var result = await _handler.Handle(new LogoutCommand(DeviceToken), CancellationToken.None);

        result.ShouldBe(Unit.Value);
        await _refreshTokenService.Received(1).RemoveRefreshTokenAsync(DeviceToken);
        await _refreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Handle_WithoutToken_RemovesNothing()
    {
        var result = await _handler.Handle(new LogoutCommand(null), CancellationToken.None);

        result.ShouldBe(Unit.Value);
        await _refreshTokenService.DidNotReceive().RemoveRefreshTokenAsync(Arg.Any<string>());
        await _refreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(Arg.Any<string>());
    }
}
