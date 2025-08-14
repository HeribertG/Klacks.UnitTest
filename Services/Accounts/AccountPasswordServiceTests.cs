using FluentAssertions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Presentation.DTOs.Registrations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace UnitTest.Services.Accounts;

[TestFixture]
public class AccountPasswordServiceTests
{
    private AccountPasswordService _passwordService;
    private IAuthenticationService _mockAuthService;
    private IUserManagementService _mockUserManagementService;
    private ILogger<AccountPasswordService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockAuthService = Substitute.For<IAuthenticationService>();
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockLogger = Substitute.For<ILogger<AccountPasswordService>>();

        _passwordService = new AccountPasswordService(_mockAuthService, _mockUserManagementService, _mockLogger);
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
}