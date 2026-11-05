// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the token choice of RefreshTokenQueryHandler: exactly one attempt. A cookie token renews the session and keeps
/// its token (no access token passed, so the user comes from the refresh token); without cookie a body token is migrated
/// to a new token (it was readable by scripts in local storage). There is no second attempt with the other token, and
/// without any token nothing is attempted.
/// </summary>

using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Accounts;

[TestFixture]
public class RefreshTokenQueryHandlerCookieTests
{
    private const string BodyToken = "body-refresh-token";
    private const string CookieToken = "cookie-refresh-token";
    private const string AccessToken = "expired-access-token";
    private const string MigratedToken = "migrated-refresh-token";

    private IAccountAuthenticationService _authenticationService = null!;
    private RefreshTokenQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _authenticationService = Substitute.For<IAccountAuthenticationService>();
        _authenticationService.RefreshTokenAsync(Arg.Any<RefreshRequestResource>()).Returns(Failed());
        _authenticationService.MigrateRefreshTokenAsync(Arg.Any<RefreshRequestResource>()).Returns(Failed());
        _handler = new RefreshTokenQueryHandler(_authenticationService, Substitute.For<ILogger<RefreshTokenQueryHandler>>());
    }

    [Test]
    public async Task CookieOnly_RefreshesWithTheCookieTokenAndReturnsTheSameToken()
    {
        _authenticationService.RefreshTokenAsync(Arg.Is<RefreshRequestResource>(r => r.RefreshToken == CookieToken))
            .Returns(Succeeded(CookieToken));

        var result = await _handler.Handle(new RefreshTokenQuery(new RefreshRequestResource(), CookieToken), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.RefreshToken.ShouldBe(CookieToken);
        result.ShouldBeOfType<TokenResource>();
        await _authenticationService.Received(1).RefreshTokenAsync(
            Arg.Is<RefreshRequestResource>(r => r.RefreshToken == CookieToken && r.Token == string.Empty));
    }

    [Test]
    public async Task CookieAndBody_UsesOnlyTheCookie_AndMakesNoSecondAttempt()
    {
        var body = new RefreshRequestResource { Token = AccessToken, RefreshToken = BodyToken };

        var result = await _handler.Handle(new RefreshTokenQuery(body, CookieToken), CancellationToken.None);

        result.ShouldBeNull();
        await _authenticationService.Received(1).RefreshTokenAsync(Arg.Any<RefreshRequestResource>());
        await _authenticationService.Received(1).RefreshTokenAsync(Arg.Is<RefreshRequestResource>(r => r.RefreshToken == CookieToken));
        await _authenticationService.DidNotReceive().MigrateRefreshTokenAsync(Arg.Any<RefreshRequestResource>());
    }

    [Test]
    public async Task CookieAndBody_Succeeding_AnswersWithoutTheTokenInTheBody_AndNeverUsesTheBodyToken()
    {
        var body = new RefreshRequestResource { Token = AccessToken, RefreshToken = BodyToken };
        _authenticationService.RefreshTokenAsync(Arg.Is<RefreshRequestResource>(r => r.RefreshToken == CookieToken))
            .Returns(Succeeded(CookieToken));

        var result = await _handler.Handle(new RefreshTokenQuery(body, CookieToken), CancellationToken.None);

        result.ShouldNotBeNull();
        result.ShouldNotBeOfType<MigrationTokenResource>();
        result!.RefreshToken.ShouldBe(CookieToken);
        await _authenticationService.DidNotReceive().RefreshTokenAsync(Arg.Is<RefreshRequestResource>(r => r.RefreshToken == BodyToken));
        await _authenticationService.DidNotReceive().MigrateRefreshTokenAsync(Arg.Any<RefreshRequestResource>());
    }

    [Test]
    public async Task BodyOnly_MigratesTheBodyTokenToANewToken()
    {
        var body = new RefreshRequestResource { Token = AccessToken, RefreshToken = BodyToken };
        _authenticationService.MigrateRefreshTokenAsync(body).Returns(Succeeded(MigratedToken));

        var result = await _handler.Handle(new RefreshTokenQuery(body, null), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.RefreshToken.ShouldBe(MigratedToken);
        result.ShouldBeOfType<MigrationTokenResource>();
        await _authenticationService.Received(1).MigrateRefreshTokenAsync(body);
        await _authenticationService.DidNotReceive().RefreshTokenAsync(Arg.Any<RefreshRequestResource>());
    }

    [Test]
    public async Task NoTokenAtAll_AttemptsNothingAndReturnsNull()
    {
        var result = await _handler.Handle(new RefreshTokenQuery(new RefreshRequestResource(), null), CancellationToken.None);

        result.ShouldBeNull();
        await _authenticationService.DidNotReceive().RefreshTokenAsync(Arg.Any<RefreshRequestResource>());
        await _authenticationService.DidNotReceive().MigrateRefreshTokenAsync(Arg.Any<RefreshRequestResource>());
    }

    private static AuthenticatedResult Failed() => new() { Success = false };

    private static AuthenticatedResult Succeeded(string refreshToken) => new()
    {
        Success = true,
        Token = "new-access-token",
        RefreshToken = refreshToken,
        Id = Guid.NewGuid().ToString(),
        Expires = DateTime.UtcNow.AddMinutes(15)
    };
}
