using FluentAssertions;
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

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
    }

    [Test]
    public async Task LogInUserAsync_WithInvalidCredentials_ShouldReturnFailureResult()
    {
        var email = "test@example.com";
        var password = "WrongPassword";

        _mockAuthService.ValidateCredentialsAsync(email, password).Returns((false, (AppUser)null));

        var result = await _authenticationService.LogInUserAsync(email, password);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task LogInUserAsync_WithNullOrEmptyEmail_ShouldReturnFailureResult()
    {
        var password = "ValidPassword123!";

        _mockAuthService.ValidateCredentialsAsync(null, password).Returns((false, (AppUser)null));
        _mockAuthService.ValidateCredentialsAsync("", password).Returns((false, (AppUser)null));

        var result1 = await _authenticationService.LogInUserAsync(null, password);
        var result2 = await _authenticationService.LogInUserAsync("", password);

        result1.Success.Should().BeFalse();
        result2.Success.Should().BeFalse();
    }

    [Test]
    public async Task LogInUserAsync_WithNullOrEmptyPassword_ShouldReturnFailureResult()
    {
        var email = "test@example.com";

        _mockAuthService.ValidateCredentialsAsync(email, null).Returns((false, (AppUser)null));
        _mockAuthService.ValidateCredentialsAsync(email, "").Returns((false, (AppUser)null));

        var result1 = await _authenticationService.LogInUserAsync(email, null);
        var result2 = await _authenticationService.LogInUserAsync(email, "");

        result1.Success.Should().BeFalse();
        result2.Success.Should().BeFalse();
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

        var result = await _authenticationService.RefreshTokenAsync(refreshRequest);

        result.Should().NotBeNull();
        await _mockAuthService.Received().GetUserFromAccessTokenAsync(refreshRequest.Token);
    }
}