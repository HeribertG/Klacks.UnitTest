using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Groups;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UnitTest.Services;

[TestFixture]
internal class GroupVisibilityServiceTests
{
    private IHttpContextAccessor _httpContextAccessor = null!;
    private DataBaseContext _dbContext = null!;
    private UserManager<AppUser> _userManager = null!;
    private IUserService _userService = null!;
    private GroupVisibilityService _groupVisibilityService = null!;

    [Test]
    public async Task IsAdmin_WhenUserIsAdmin_ReturnsTrue()
    {
        // Arrange
        _userService.IsAdmin().Returns(Task.FromResult(true));

        // Act
        var result = await _groupVisibilityService.IsAdmin();

        // Assert
        result.Should().BeTrue();
        await _userService.Received(1).IsAdmin();
    }

    [Test]
    public async Task IsAdmin_WhenUserIsNotAdmin_ReturnsFalse()
    {
        // Arrange
        _userService.IsAdmin().Returns(Task.FromResult(false));

        // Act
        var result = await _groupVisibilityService.IsAdmin();

        // Assert
        result.Should().BeFalse();
        await _userService.Received(1).IsAdmin();
    }

    [Test]
    public async Task ReadVisibleRootIdList_WhenUserIsAdmin_ReturnsEmptyList()
    {
        // Arrange
        _userService.IsAdmin().Returns(Task.FromResult(true));
        _userService.GetIdString().Returns("admin-user-id");

        // Act
        var result = await _groupVisibilityService.ReadVisibleRootIdList();

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public async Task ReadVisibleRootIdList_WhenUserIsNotAdmin_ReturnsUserGroupIds()
    {
        // Arrange
        var userId = "regular-user-id";
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();

        _userService.IsAdmin().Returns(Task.FromResult(false));
        _userService.GetIdString().Returns(userId);

        // Seed test data
        var groupVisibilities = new List<GroupVisibility>
        {
            new GroupVisibility
            {
                Id = Guid.NewGuid(),
                AppUserId = userId,
                GroupId = groupId1
            },
            new GroupVisibility
            {
                Id = Guid.NewGuid(),
                AppUserId = userId,
                GroupId = groupId2
            },
            new GroupVisibility
            {
                Id = Guid.NewGuid(),
                AppUserId = "other-user-id",
                GroupId = Guid.NewGuid()
            }
        };

        _dbContext.GroupVisibility.AddRange(groupVisibilities);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _groupVisibilityService.ReadVisibleRootIdList();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(groupId1);
        result.Should().Contain(groupId2);
    }

    [Test]
    public async Task ReadVisibleRootIdList_WhenUserIdIsEmpty_ReturnsEmptyList()
    {
        // Arrange
        _userService.IsAdmin().Returns(Task.FromResult(false));
        _userService.GetIdString().Returns(string.Empty);

        // Act
        var result = await _groupVisibilityService.ReadVisibleRootIdList();

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public async Task ReadAdmins_ReturnsAdminUserIds()
    {
        // Arrange
        var adminUser1 = new AppUser
        {
            Id = "admin-1",
            UserName = "admin1@test.com",
            Email = "admin1@test.com",
            EmailConfirmed = true
        };

        var adminUser2 = new AppUser
        {
            Id = "admin-2",
            UserName = "admin2@test.com",
            Email = "admin2@test.com",
            EmailConfirmed = true
        };

        var regularUser = new AppUser
        {
            Id = "user-1",
            UserName = "user1@test.com",
            Email = "user1@test.com",
            EmailConfirmed = true
        };

        _dbContext.AppUser.AddRange(adminUser1, adminUser2, regularUser);
        await _dbContext.SaveChangesAsync();

        // Mock UserManager to return admin users
        _userManager.GetUsersInRoleAsync(Roles.Admin)
                   .Returns(Task.FromResult<IList<AppUser>>(new List<AppUser> { adminUser1, adminUser2 }));

        // Act
        var result = await _groupVisibilityService.ReadAdmins();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain("admin-1");
        result.Should().Contain("admin-2");
        result.Should().NotContain("user-1");
    }

    [Test]
    public async Task ReviseAdminVisibility_RemovesAdminUsersAndAddsThemBack()
    {
        // Arrange
        var adminUserId = "admin-user-id";
        var regularUserId = "regular-user-id";
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();
        var rootGroupId = Guid.NewGuid();

        var adminUser = new AppUser
        {
            Id = adminUserId,
            UserName = "admin@test.com",
            Email = "admin@test.com",
            EmailConfirmed = true
        };

        var regularUser = new AppUser
        {
            Id = regularUserId,
            UserName = "user@test.com",
            Email = "user@test.com",
            EmailConfirmed = true
        };

        // Add users to database
        _dbContext.AppUser.AddRange(adminUser, regularUser);

        // Add groups to database
        var rootGroup = new Group
        {
            Id = rootGroupId,
            Name = "Root Group",
            ValidFrom = DateTime.Now,
            Root = rootGroupId // Self-referencing root
        };
        _dbContext.Group.Add(rootGroup);
        await _dbContext.SaveChangesAsync();


        var initialList = new List<GroupVisibility>
        {
            new GroupVisibility
            {
                Id = Guid.NewGuid(),
                AppUserId = adminUserId,
                GroupId = groupId1
            },
            new GroupVisibility
            {
                Id = Guid.NewGuid(),
                AppUserId = regularUserId,
                GroupId = groupId2
            }
        };


        _userManager.FindByIdAsync(adminUserId).Returns(Task.FromResult(adminUser));
        _userManager.FindByIdAsync(regularUserId).Returns(Task.FromResult(regularUser));
        _userManager.GetRolesAsync(adminUser).Returns(Task.FromResult<IList<string>>(new List<string> { Roles.Admin }));
        _userManager.GetRolesAsync(regularUser).Returns(Task.FromResult<IList<string>>(new List<string> { "User" }));
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(Task.FromResult<IList<AppUser>>(new List<AppUser> { adminUser }));

        // Act
        var result = await _groupVisibilityService.ReviseAdminVisibility(initialList);

        // Assert
        result.Should().NotBeEmpty();

        result.Should().NotContain(gv => gv.AppUserId == adminUserId && gv.GroupId == groupId1);

        result.Should().Contain(gv => gv.AppUserId == regularUserId && gv.GroupId == groupId2);

        result.Should().Contain(gv => gv.AppUserId == adminUserId && gv.GroupId == rootGroupId);
    }

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _dbContext = new DataBaseContext(options, _httpContextAccessor);
        _dbContext.Database.EnsureCreated();

        var userStore = Substitute.For<IUserStore<AppUser>>();
        var options2 = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errors = Substitute.For<IdentityErrorDescriber>();
        var services = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore, options2, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errors, services, logger);

        _userService = Substitute.For<IUserService>();

        _groupVisibilityService = new GroupVisibilityService(_dbContext, _userManager, _userService);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
        _userManager?.Dispose();
    }
}