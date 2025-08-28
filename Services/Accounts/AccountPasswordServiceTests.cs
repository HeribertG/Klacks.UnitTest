using FluentAssertions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Presentation.DTOs.Registrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace UnitTest.Services.Accounts;

[TestFixture]
public class AccountPasswordServiceTests
{
    private AccountPasswordService _passwordService;
    private IAuthenticationService _mockAuthService;
    private IUserManagementService _mockUserManagementService;
    private IAccountNotificationService _mockNotificationService;
    private IServiceProvider _mockServiceProvider;
    private ILogger<AccountPasswordService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockAuthService = Substitute.For<IAuthenticationService>();
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockNotificationService = Substitute.For<IAccountNotificationService>();
        _mockServiceProvider = Substitute.For<IServiceProvider>();
        _mockLogger = Substitute.For<ILogger<AccountPasswordService>>();

        // Setup service provider to return the notification service when requested
        _mockServiceProvider.GetService<IAccountNotificationService>().Returns(_mockNotificationService);

        _passwordService = new AccountPasswordService(_mockAuthService, _mockUserManagementService, _mockServiceProvider, _mockLogger);
    }

    [Test]
    public async Task ChangePasswordAsync_WithValidCredentials_ShouldReturnSuccess()
    {
        var changePasswordResource = new ChangePasswordResource
        {
            Email = "test@example.com",
            OldPassword = "OldPassword123!",
            Password = "NewPassword123!"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = changePasswordResource.Email,
            UserName = changePasswordResource.Email,
            FirstName = "John",
            LastName = "Doe"
        };

        _mockUserManagementService.FindUserByEmailAsync(changePasswordResource.Email).Returns(testUser);
        _mockAuthService.ValidateCredentialsAsync(changePasswordResource.Email, changePasswordResource.OldPassword)
            .Returns((true, testUser));
        _mockAuthService.ChangePasswordAsync(testUser, changePasswordResource.OldPassword, changePasswordResource.Password)
            .Returns((true, null));

        var result = await _passwordService.ChangePasswordAsync(changePasswordResource);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
    }

    [Test]
    public async Task ChangePasswordAsync_WithInvalidEmail_ShouldReturnFailure()
    {
        var changePasswordResource = new ChangePasswordResource
        {
            Email = "nonexistent@example.com",
            OldPassword = "OldPassword123!",
            Password = "NewPassword123!"
        };

        _mockUserManagementService.FindUserByEmailAsync(changePasswordResource.Email).Returns((AppUser)null);

        var result = await _passwordService.ChangePasswordAsync(changePasswordResource);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task ChangePasswordAsync_WithInvalidOldPassword_ShouldReturnFailure()
    {
        var changePasswordResource = new ChangePasswordResource
        {
            Email = "test@example.com",
            OldPassword = "WrongOldPassword",
            Password = "NewPassword123!"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = changePasswordResource.Email,
            UserName = changePasswordResource.Email
        };

        _mockUserManagementService.FindUserByEmailAsync(changePasswordResource.Email).Returns(testUser);
        _mockAuthService.ValidateCredentialsAsync(changePasswordResource.Email, changePasswordResource.OldPassword)
            .Returns((false, (AppUser)null));

        var result = await _passwordService.ChangePasswordAsync(changePasswordResource);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task ChangePasswordAsync_WithChangePasswordFailure_ShouldReturnFailure()
    {
        var changePasswordResource = new ChangePasswordResource
        {
            Email = "test@example.com",
            OldPassword = "OldPassword123!",
            Password = "weak"
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = changePasswordResource.Email,
            UserName = changePasswordResource.Email
        };

        _mockUserManagementService.FindUserByEmailAsync(changePasswordResource.Email).Returns(testUser);
        _mockAuthService.ValidateCredentialsAsync(changePasswordResource.Email, changePasswordResource.OldPassword)
            .Returns((true, testUser));
        _mockAuthService.ChangePasswordAsync(testUser, changePasswordResource.OldPassword, changePasswordResource.Password)
            .Returns((false, null));

        var result = await _passwordService.ChangePasswordAsync(changePasswordResource);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
    }

    [Test]
    public async Task GeneratePasswordResetTokenAsync_WithExistingEmail_ShouldReturnTrue()
    {
        var email = "test@example.com";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            UserName = email
        };

        _mockUserManagementService.FindUserByEmailAsync(email).Returns(testUser);
        _mockUserManagementService.UpdateUserAsync(Arg.Any<AppUser>()).Returns((true, null));
        _mockNotificationService.SendEmailAsync(Arg.Any<string>(), email, Arg.Any<string>()).Returns("true");

        var result = await _passwordService.GeneratePasswordResetTokenAsync(email);

        result.Should().BeTrue();
        await _mockNotificationService.Received(1).SendEmailAsync(Arg.Any<string>(), email, Arg.Any<string>());
    }

    [Test]
    public async Task GeneratePasswordResetTokenAsync_WithNonExistentEmail_ShouldReturnFalse()
    {
        var email = "nonexistent@example.com";

        _mockUserManagementService.FindUserByEmailAsync(email).Returns((AppUser)null);

        var result = await _passwordService.GeneratePasswordResetTokenAsync(email);

        result.Should().BeFalse();
        await _mockNotificationService.DidNotReceive().SendEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ValidatePasswordResetTokenAsync_WithValidToken_ShouldReturnTrue()
    {
        var token = "validtoken";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            PasswordResetToken = token,
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1)
        };

        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);

        var result = await _passwordService.ValidatePasswordResetTokenAsync(token);

        result.Should().BeTrue();
    }

    [Test]
    public async Task ValidatePasswordResetTokenAsync_WithExpiredToken_ShouldReturnFalse()
    {
        var token = "expiredtoken";
        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            PasswordResetToken = token,
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(-1) // Expired
        };

        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);

        var result = await _passwordService.ValidatePasswordResetTokenAsync(token);

        result.Should().BeFalse();
    }

    [Test]
    public async Task ResetPasswordAsync_WithValidToken_ShouldReturnSuccess()
    {
        var token = "validtoken";
        var newPassword = "NewPassword123!";
        var resetPasswordResource = new ResetPasswordResource
        {
            Token = token,
            Password = newPassword
        };

        var testUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@example.com",
            PasswordResetToken = token,
            PasswordResetTokenExpires = DateTime.UtcNow.AddHours(1)
        };

        _mockUserManagementService.FindUserByTokenAsync(token).Returns(testUser);
        _mockAuthService.ResetPasswordAsync(testUser, token, newPassword).Returns((true, null));
        _mockUserManagementService.UpdateUserAsync(Arg.Any<AppUser>()).Returns((true, null));

        var result = await _passwordService.ResetPasswordAsync(resetPasswordResource);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
    }
}