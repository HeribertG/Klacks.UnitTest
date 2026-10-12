// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GroupVisibilityService.GetVisibilityScopeForUserAsync resolves the visibility scope for an explicitly named user
/// with the same fail-closed rule as the ambient GetVisibilityScopeAsync: admin and an installation without groups are
/// unrestricted, a non-admin is limited to the subtrees of their rows and sees nothing without one; an empty or
/// unknown user id sees nothing even without groups.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services;

[TestFixture]
internal class GroupVisibilityServiceUserScopeTests
{
    private const string PlannerId = "planner-user-id";
    private const string AdminId = "admin-user-id";

    private DataBaseContext _dbContext = null!;
    private UserManager<AppUser> _userManager = null!;
    private IUserService _userService = null!;
    private GroupVisibilityService _service = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _dbContext.Database.EnsureCreated();

        _userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(), Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(), new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(), Substitute.For<ILookupNormalizer>(),
            Substitute.For<IdentityErrorDescriber>(), Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());

        RegisterUser(PlannerId, isAdmin: false);
        RegisterUser(AdminId, isAdmin: true);

        _userService = Substitute.For<IUserService>();
        _service = new GroupVisibilityService(_dbContext, _userManager, _userService);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _userManager.Dispose();
    }

    private void RegisterUser(string id, bool isAdmin)
    {
        var user = new AppUser { Id = id };
        _userManager.FindByIdAsync(id).Returns(user);
        _userManager.IsInRoleAsync(user, Roles.Admin).Returns(isAdmin);
    }

    private async Task<(Guid RootId, Guid ChildId, Guid ForeignRootId)> SeedGroupsAsync()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var foreignRootId = Guid.NewGuid();
        _dbContext.Group.AddRange(
            new Group { Id = rootId, Name = "Bern", ValidFrom = DateTime.UtcNow },
            new Group { Id = childId, Name = "Bern Nord", ValidFrom = DateTime.UtcNow, Parent = rootId, Root = rootId },
            new Group { Id = foreignRootId, Name = "Zuerich", ValidFrom = DateTime.UtcNow });
        await _dbContext.SaveChangesAsync();
        return (rootId, childId, foreignRootId);
    }

    [Test]
    public async Task EmptyUserId_SeesNothing_EvenWithoutGroups()
    {
        var scope = await _service.GetVisibilityScopeForUserAsync(string.Empty);

        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleGroupIds.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownUser_SeesNothing()
    {
        await SeedGroupsAsync();

        var scope = await _service.GetVisibilityScopeForUserAsync("unknown-user-id");

        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleGroupIds.ShouldBeEmpty();
    }

    [Test]
    public async Task Admin_IsUnrestricted()
    {
        await SeedGroupsAsync();

        var scope = await _service.GetVisibilityScopeForUserAsync(AdminId);

        scope.IsUnrestricted.ShouldBeTrue();
    }

    [Test]
    public async Task NonAdmin_WithoutGroupsInTheInstallation_IsUnrestricted()
    {
        var scope = await _service.GetVisibilityScopeForUserAsync(PlannerId);

        scope.IsUnrestricted.ShouldBeTrue();
    }

    [Test]
    public async Task NonAdmin_WithoutVisibilityRows_SeesNothing()
    {
        await SeedGroupsAsync();

        var scope = await _service.GetVisibilityScopeForUserAsync(PlannerId);

        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleRootIds.ShouldBeEmpty();
        scope.VisibleGroupIds.ShouldBeEmpty();
    }

    [Test]
    public async Task NonAdmin_WithRow_SeesTheSubtreeOfTheVisibleRootOnly()
    {
        var (rootId, childId, foreignRootId) = await SeedGroupsAsync();
        _dbContext.GroupVisibility.AddRange(
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = PlannerId, GroupId = rootId },
            new GroupVisibility { Id = Guid.NewGuid(), AppUserId = AdminId, GroupId = foreignRootId });
        await _dbContext.SaveChangesAsync();

        var scope = await _service.GetVisibilityScopeForUserAsync(PlannerId);

        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleRootIds.ShouldBe(new[] { rootId });
        scope.VisibleGroupIds.ShouldBe(new[] { rootId, childId }, ignoreOrder: true);
    }

    [Test]
    public async Task ExplicitUser_IsIndependentOfTheAmbientCaller()
    {
        await SeedGroupsAsync();
        _userService.IsAdmin().Returns(true);
        _userService.GetIdString().Returns(AdminId);

        var scope = await _service.GetVisibilityScopeForUserAsync(PlannerId);

        scope.IsUnrestricted.ShouldBeFalse();
    }
}
