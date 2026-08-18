using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Application.DTOs;
using Klacks.Api.Application.DTOs.Registrations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountManagementServiceTests
{
    private AccountManagementService _managementService;
    private IUserManagementService _mockUserManagementService;
    private ILogger<AccountManagementService> _mockLogger;
    private DataBaseContext _dbContext;

    [SetUp]
    public void SetUp()
    {
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockLogger = Substitute.For<ILogger<AccountManagementService>>();

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _managementService = new AccountManagementService(_mockUserManagementService, _dbContext, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
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
            Email = u.Email!,
            FirstName = u.FirstName,
            LastName = u.LastName
        }).ToList());

        var result = await _managementService.GetUserListAsync();

        result.ShouldNotBeNull();
        result.Count().ShouldBe(2);
        result.First().FirstName.ShouldBe("John");
        result.First().LastName.ShouldBe("Doe");
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

        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.Messages.ShouldContain("Role changed successfully");
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

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        result.Messages.ShouldContain("User not found");
    }

    [Test]
    public async Task UpdateAccountAsync_TrimsPhoneNumberWhitespace()
    {
        var userId = Guid.NewGuid();
        var existingUser = new AppUser { Id = userId.ToString(), FirstName = "John", LastName = "Doe", Email = "john@example.com" };
        AppUser? capturedUser = null;

        _mockUserManagementService.FindUserByIdAsync(userId.ToString()).Returns(existingUser);
        _mockUserManagementService.UpdateUserAsync(Arg.Do<AppUser>(u => capturedUser = u))
            .Returns((true, (Microsoft.AspNetCore.Identity.IdentityResult?)null));

        var updateAccount = new UpdateAccountResource
        {
            Id = userId,
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            UserName = "john.doe",
            PhoneNumber = "  +41 79 111 22 33  "
        };

        var result = await _managementService.UpdateAccountAsync(updateAccount);

        result.Success.ShouldBeTrue();
        capturedUser.ShouldNotBeNull();
        capturedUser!.PhoneNumber.ShouldBe("+41 79 111 22 33");
    }

    [Test]
    public async Task UpdateAccountAsync_BlankPhoneNumber_IsStoredAsNull()
    {
        var userId = Guid.NewGuid();
        var existingUser = new AppUser { Id = userId.ToString(), FirstName = "John", LastName = "Doe", Email = "john@example.com", PhoneNumber = "+41 79 000 00 00" };
        AppUser? capturedUser = null;

        _mockUserManagementService.FindUserByIdAsync(userId.ToString()).Returns(existingUser);
        _mockUserManagementService.UpdateUserAsync(Arg.Do<AppUser>(u => capturedUser = u))
            .Returns((true, (Microsoft.AspNetCore.Identity.IdentityResult?)null));

        var updateAccount = new UpdateAccountResource
        {
            Id = userId,
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com",
            UserName = "john.doe",
            PhoneNumber = "   "
        };

        await _managementService.UpdateAccountAsync(updateAccount);

        capturedUser.ShouldNotBeNull();
        capturedUser!.PhoneNumber.ShouldBeNull();
    }

    [Test]
    public async Task ReorderUsersAsync_SetsDisplayOrderToSubmittedPosition()
    {
        var userA = new AppUser { Id = "user-a", FirstName = "A", LastName = "A", DisplayOrder = 5 };
        var userB = new AppUser { Id = "user-b", FirstName = "B", LastName = "B", DisplayOrder = 6 };
        var userC = new AppUser { Id = "user-c", FirstName = "C", LastName = "C", DisplayOrder = 7 };
        _dbContext.AppUser.AddRange(userA, userB, userC);
        await _dbContext.SaveChangesAsync();

        var result = await _managementService.ReorderUsersAsync(new[] { "user-c", "user-a", "user-b" });

        result.Success.ShouldBeTrue();
        var reordered = await _dbContext.AppUser.ToDictionaryAsync(u => u.Id);
        reordered["user-c"].DisplayOrder.ShouldBe(1);
        reordered["user-a"].DisplayOrder.ShouldBe(2);
        reordered["user-b"].DisplayOrder.ShouldBe(3);
    }

    [Test]
    public async Task ReorderUsersAsync_UnknownUserIdInList_IsSilentlyIgnored()
    {
        var userA = new AppUser { Id = "user-a", FirstName = "A", LastName = "A", DisplayOrder = 1 };
        _dbContext.AppUser.Add(userA);
        await _dbContext.SaveChangesAsync();

        var result = await _managementService.ReorderUsersAsync(new[] { "user-ghost", "user-a" });

        result.Success.ShouldBeTrue();
        var reordered = await _dbContext.AppUser.SingleAsync(u => u.Id == "user-a");
        reordered.DisplayOrder.ShouldBe(2);
    }
}