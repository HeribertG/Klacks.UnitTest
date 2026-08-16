using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Application.DTOs.Registrations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountAuthenticationServiceTests
{
    private AccountAuthenticationService _authenticationService;
    private ITokenService _mockTokenService;
    private IAuthenticationService _mockAuthService;
    private IUserManagementService _mockUserManagementService;
    private IRefreshTokenService _mockRefreshTokenService;
    private ILogger<AccountAuthenticationService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockTokenService = Substitute.For<ITokenService>();
        _mockAuthService = Substitute.For<IAuthenticationService>();
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockRefreshTokenService = Substitute.For<IRefreshTokenService>();
        _mockLogger = Substitute.For<ILogger<AccountAuthenticationService>>();

        _authenticationService = new AccountAuthenticationService(
            _mockTokenService,
            _mockAuthService,
            _mockUserManagementService,
            _mockRefreshTokenService,
            _mockLogger);
    }

    [Test]
    public async Task LogInUserAsync_WithValidCredentials_ShouldReturnSuccessResult()
    {
        var email = "test@example.com";
        var password = "ValidPassword123!";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            UserName = email,
            FirstName = "John",
            LastName = "Doe"
        };

        _mockAuthService.ValidateCredentialsAsync(email, password).Returns((true, testUser));

        var result = await _authenticationService.LogInUserAsync(email, password);

        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task LogInUserAsync_WithInvalidCredentials_ShouldReturnFailureResult()
    {
        var email = "test@example.com";
        var password = "WrongPassword";

        _mockAuthService.ValidateCredentialsAsync(email, password).Returns((false, (AppUser?)null));

        var result = await _authenticationService.LogInUserAsync(email, password);

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task LogInUserAsync_WithNullOrEmptyEmail_ShouldReturnFailureResult()
    {
        var password = "ValidPassword123!";

        _mockAuthService.ValidateCredentialsAsync(null!, password).Returns((false, (AppUser?)null));
        _mockAuthService.ValidateCredentialsAsync("", password).Returns((false, (AppUser?)null));

        var result1 = await _authenticationService.LogInUserAsync(null!, password);
        var result2 = await _authenticationService.LogInUserAsync("", password);

        result1.Success.ShouldBeFalse();
        result2.Success.ShouldBeFalse();
    }

    [Test]
    public async Task LogInUserAsync_WithNullOrEmptyPassword_ShouldReturnFailureResult()
    {
        var email = "test@example.com";

        _mockAuthService.ValidateCredentialsAsync(email, null!).Returns((false, (AppUser?)null));
        _mockAuthService.ValidateCredentialsAsync(email, "").Returns((false, (AppUser?)null));

        var result1 = await _authenticationService.LogInUserAsync(email, null!);
        var result2 = await _authenticationService.LogInUserAsync(email, "");

        result1.Success.ShouldBeFalse();
        result2.Success.ShouldBeFalse();
    }

    [Test]
    public async Task RefreshTokenAsync_WithValidToken_ShouldCallCorrectServices()
    {
        var refreshRequest = new RefreshRequestResource
        {
            Token = "valid-jwt-token",
            RefreshToken = "valid-refresh-token"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            UserName = "test@example.com"
        };

        _mockAuthService.GetUserFromAccessTokenAsync(refreshRequest.Token).Returns(testUser);
        _mockRefreshTokenService.ValidateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns(true);
        _mockRefreshTokenService.RotateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns("new-refresh-token");

        var result = await _authenticationService.RefreshTokenAsync(refreshRequest);

        result.ShouldNotBeNull();
        await _mockAuthService.Received().GetUserFromAccessTokenAsync(refreshRequest.Token);
    }

    [Test]
    public async Task RefreshTokenAsync_WithValidToken_ShouldRotateInsteadOfRecreate()
    {
        var refreshRequest = new RefreshRequestResource
        {
            Token = "valid-jwt-token",
            RefreshToken = "old-refresh-token"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            UserName = "test@example.com"
        };

        _mockAuthService.GetUserFromAccessTokenAsync(refreshRequest.Token).Returns(testUser);
        _mockRefreshTokenService.ValidateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns(true);
        _mockRefreshTokenService.RotateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns("new-refresh-token");

        var result = await _authenticationService.RefreshTokenAsync(refreshRequest);

        result.RefreshToken.ShouldBe("new-refresh-token");
        await _mockRefreshTokenService.Received(1).RotateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken);
        await _mockRefreshTokenService.DidNotReceive().CreateRefreshTokenAsync(Arg.Any<string>());
        await _mockRefreshTokenService.DidNotReceive().RemoveAllUserRefreshTokensAsync(Arg.Any<string>());
    }

    [Test]
    public async Task RefreshTokenAsync_WhenAccountIsBlocked_IsRefusedAndIssuesNoNewToken()
    {
        var refreshRequest = new RefreshRequestResource
        {
            Token = "valid-jwt-token",
            RefreshToken = "valid-refresh-token"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "deactivated@example.com",
            UserName = "deactivated@example.com",
            DeactivatedAt = DateTime.UtcNow.AddHours(-1)
        };

        _mockAuthService.GetUserFromAccessTokenAsync(refreshRequest.Token).Returns(testUser);
        _mockUserManagementService.IsAccountBlockedAsync(testUser).Returns(true);
        _mockRefreshTokenService.ValidateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns(true);

        var result = await _authenticationService.RefreshTokenAsync(refreshRequest);

        // Without this gate a deactivated or locked-out account keeps renewing its session forever:
        // refreshing never passes the credential check that would otherwise refuse it.
        result.Success.ShouldBeFalse();
        result.Token.ShouldBeNullOrEmpty();
        await _mockRefreshTokenService.DidNotReceive().RotateRefreshTokenAsync(Arg.Any<string>(), Arg.Any<string>());
        await _mockRefreshTokenService.DidNotReceive().CreateRefreshTokenAsync(Arg.Any<string>());
        await _mockTokenService.DidNotReceive().CreateToken(Arg.Any<AppUser>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task RefreshTokenAsync_WhenAccountIsBlockedViaTheRefreshTokenLookup_IsAlsoRefused()
    {
        var refreshRequest = new RefreshRequestResource
        {
            Token = "expired-jwt-token",
            RefreshToken = "valid-refresh-token"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "lockedout@example.com",
            UserName = "lockedout@example.com"
        };

        // The access token no longer resolves, so the user is found through the refresh token itself.
        _mockAuthService.GetUserFromAccessTokenAsync(refreshRequest.Token).Returns((AppUser?)null);
        _mockRefreshTokenService.GetUserFromRefreshTokenAsync(refreshRequest.RefreshToken).Returns(testUser);
        _mockUserManagementService.IsAccountBlockedAsync(testUser).Returns(true);
        _mockRefreshTokenService.ValidateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns(true);

        var result = await _authenticationService.RefreshTokenAsync(refreshRequest);

        result.Success.ShouldBeFalse();
        await _mockRefreshTokenService.DidNotReceive().RotateRefreshTokenAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task RefreshTokenAsync_WhenAccountIsActive_StillRefreshes()
    {
        var refreshRequest = new RefreshRequestResource
        {
            Token = "valid-jwt-token",
            RefreshToken = "valid-refresh-token"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "active@example.com",
            UserName = "active@example.com"
        };

        _mockAuthService.GetUserFromAccessTokenAsync(refreshRequest.Token).Returns(testUser);
        _mockUserManagementService.IsAccountBlockedAsync(testUser).Returns(false);
        _mockRefreshTokenService.ValidateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns(true);
        _mockRefreshTokenService.RotateRefreshTokenAsync(testUser.Id, refreshRequest.RefreshToken).Returns("new-refresh-token");

        var result = await _authenticationService.RefreshTokenAsync(refreshRequest);

        result.Success.ShouldBeTrue();
        result.RefreshToken.ShouldBe("new-refresh-token");
    }
}