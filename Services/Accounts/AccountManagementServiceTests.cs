using FluentAssertions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Presentation.DTOs;
using Klacks.Api.Presentation.DTOs.Registrations;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountManagementServiceTests
{
    private AccountManagementService _managementService;
    private IUserManagementService _mockUserManagementService;
    private ILogger<AccountManagementService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockLogger = Substitute.For<ILogger<AccountManagementService>>();

        _managementService = new AccountManagementService(_mockUserManagementService, _mockLogger);
    }

    [Test]
    public async Task GetUserListAsync_ShouldReturnUserList()
    {
        var users = new List<AppUser>
        {
            new AppUser 
            { 
                Id = Guid.NewGuid().ToString(), 
                Email = "user1@example.com", 
                FirstName = "John", 
                LastName = "Doe"
            },
            new AppUser 
            { 
                Id = Guid.NewGuid().ToString(), 
                Email = "user2@example.com", 
                FirstName = "Jane", 
                LastName = "Smith"
            }
        };

        _mockUserManagementService.GetUserListAsync().Returns(users.Select(u => new UserResource
        {
            Id = u.Id,
            Email = u.Email,
            FirstName = u.FirstName,
            LastName = u.LastName
        }).ToList());

        var result = await _managementService.GetUserListAsync();

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.First().FirstName.Should().Be("John");
        result.First().LastName.Should().Be("Doe");
    }

    [Test]
    public async Task ChangeRoleUserAsync_WithValidUser_ShouldReturnSuccess()
    {
        var changeRole = new ChangeRole
        {
            UserId = Guid.NewGuid().ToString(),
            RoleName = "Admin",
            IsSelected = true
        };

        _mockUserManagementService.ChangeUserRoleAsync(changeRole.UserId, changeRole.RoleName, changeRole.IsSelected)
            .Returns((true, "Role changed successfully"));

        var result = await _managementService.ChangeRoleUserAsync(changeRole);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Messages.Should().Contain("Role changed successfully");
    }

    [Test]
    public async Task ChangeRoleUserAsync_WithFailure_ShouldReturnFailure()
    {
        var changeRole = new ChangeRole
        {
            UserId = Guid.NewGuid().ToString(),
            RoleName = "Admin",
            IsSelected = true
        };

        _mockUserManagementService.ChangeUserRoleAsync(changeRole.UserId, changeRole.RoleName, changeRole.IsSelected)
            .Returns((false, "User not found"));

        var result = await _managementService.ChangeRoleUserAsync(changeRole);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Messages.Should().Contain("User not found");
    }

    [Test]
    public async Task DeleteAccountUserAsync_WithValidUser_ShouldReturnSuccess()
    {
        var userId = Guid.NewGuid();

        _mockUserManagementService.DeleteUserAsync(userId).Returns((true, "User deleted successfully"));

        var result = await _managementService.DeleteAccountUserAsync(userId);

        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Messages.Should().Contain("User deleted successfully");
    }

    [Test]
    public async Task DeleteAccountUserAsync_WithFailure_ShouldReturnFailure()
    {
        var userId = Guid.NewGuid();

        _mockUserManagementService.DeleteUserAsync(userId).Returns((false, "User not found"));

        var result = await _managementService.DeleteAccountUserAsync(userId);

        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Messages.Should().Contain("User not found");
    }
}