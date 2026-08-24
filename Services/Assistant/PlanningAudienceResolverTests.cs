// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PlanningAudienceResolver's group-scoped audience resolution: Admins stay
/// unrestricted, an Authorised planner is included only when their GroupVisibility (including the
/// group's Nested Set subtree, via its root) covers the event's group, a planner with zero
/// GroupVisibility rows is excluded (fail-closed rather than "sees everything"), synthetic rows for
/// other users never widen an unrelated planner's scope, an unresolvable group falls back to
/// admins-only, and the result is cached like the existing planner/admin sets.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PlanningAudienceResolverTests
{
    private UserManager<AppUser> _userManager = null!;
    private IMemoryCache _cache = null!;
    private IGroupVisibilityRepository _groupVisibilityRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private PlanningAudienceResolver _sut = null!;

    private static readonly Guid RootGroupId = Guid.NewGuid();
    private static readonly Guid ChildGroupId = Guid.NewGuid();
    private static readonly Guid ForeignRootGroupId = Guid.NewGuid();

    private const string AdminUserId = "admin-1";
    private const string ScopedPlannerUserId = "planner-scoped";
    private const string ForeignScopedPlannerUserId = "planner-foreign";
    private const string NoRowPlannerUserId = "planner-no-row";

    [SetUp]
    public void Setup()
    {
        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errors = Substitute.For<IdentityErrorDescriber>();
        var services = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errors, services, logger);

        _cache = new MemoryCache(new MemoryCacheOptions());
        _groupVisibilityRepository = Substitute.For<IGroupVisibilityRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();

        _sut = new PlanningAudienceResolver(_userManager, _cache, _groupVisibilityRepository, _groupRepository);
    }

    [TearDown]
    public void TearDown()
    {
        _userManager?.Dispose();
        _cache?.Dispose();
    }

    private void SetupRoles(IList<AppUser> admins, IList<AppUser> authorised)
    {
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(Task.FromResult(admins));
        _userManager.GetUsersInRoleAsync(Roles.Authorised).Returns(Task.FromResult(authorised));
    }

    private static AppUser MakeUser(string id) => new() { Id = id, UserName = id, Email = $"{id}@test.local" };

    private void SetupGroup(Guid groupId, Guid? root) =>
        _groupRepository.GetNoTracking(groupId).Returns(new Group { Id = groupId, Root = root });

    private void SetupVisibility(string userId, params Guid[] visibleRootIds)
    {
        var rows = visibleRootIds
            .Select(id => new GroupVisibility { AppUserId = userId, GroupId = id })
            .ToList();
        _groupVisibilityRepository.GroupVisibilityList(userId).Returns(rows);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_Admin_IsAlwaysIncluded_RegardlessOfVisibility()
    {
        var admin = MakeUser(AdminUserId);
        SetupRoles(new List<AppUser> { admin }, new List<AppUser>());
        SetupGroup(RootGroupId, null);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        result.ShouldContain(AdminUserId);
        await _groupVisibilityRepository.DidNotReceive().GroupVisibilityList(AdminUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_AuthorisedUserWithMatchingRootVisibility_IsIncluded()
    {
        var planner = MakeUser(ScopedPlannerUserId);
        SetupRoles(new List<AppUser>(), new List<AppUser> { planner });
        SetupGroup(RootGroupId, null);
        SetupVisibility(ScopedPlannerUserId, RootGroupId);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        result.ShouldContain(ScopedPlannerUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_AuthorisedUserWithForeignGroupVisibility_IsExcluded()
    {
        // NEGATIVE TEST (DoD 3e: "User sieht fremde Gruppe NICHT"). A planner scoped to a different
        // group tree must never receive an event for this group.
        var planner = MakeUser(ForeignScopedPlannerUserId);
        SetupRoles(new List<AppUser>(), new List<AppUser> { planner });
        SetupGroup(RootGroupId, null);
        SetupVisibility(ForeignScopedPlannerUserId, ForeignRootGroupId);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        result.ShouldNotContain(ForeignScopedPlannerUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_AuthorisedUserWithNoVisibilityRows_IsExcluded_FailClosed()
    {
        var planner = MakeUser(NoRowPlannerUserId);
        SetupRoles(new List<AppUser>(), new List<AppUser> { planner });
        SetupGroup(RootGroupId, null);
        SetupVisibility(NoRowPlannerUserId);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        result.ShouldNotContain(NoRowPlannerUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_ChildGroupOfVisibleRoot_IsIncluded_SubtreeInheritance()
    {
        // Subtree grant is root-tree-only, matching GroupVisibilityService.GetVisibilityScopeAsync:
        // a GroupVisibility row always names a tree ROOT, and covers every group whose Root points
        // back to it -- not arbitrary mid-tree nodes.
        var planner = MakeUser(ScopedPlannerUserId);
        SetupRoles(new List<AppUser>(), new List<AppUser> { planner });
        SetupGroup(ChildGroupId, RootGroupId);
        SetupVisibility(ScopedPlannerUserId, RootGroupId);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(ChildGroupId);

        result.ShouldContain(ScopedPlannerUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_UnknownOrDeletedGroup_ReturnsAdminsOnly()
    {
        var admin = MakeUser(AdminUserId);
        var planner = MakeUser(ScopedPlannerUserId);
        SetupRoles(new List<AppUser> { admin }, new List<AppUser> { planner });
        _groupRepository.GetNoTracking(RootGroupId).Returns((Group?)null);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        result.ShouldBe(new[] { AdminUserId });
        await _groupVisibilityRepository.DidNotReceive().GroupVisibilityList(Arg.Any<string>());
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_ForeignUsersVisibilityRows_DoNotWidenScope()
    {
        // Mirrors GroupScopeGuardTests: GroupVisibilityList also carries rows for other users (the
        // repository injects synthetic all-roots rows per Admin via ReviseAdminVisibility). Only rows
        // whose AppUserId matches the queried planner may grant access.
        var planner = MakeUser(NoRowPlannerUserId);
        SetupRoles(new List<AppUser>(), new List<AppUser> { planner });
        SetupGroup(RootGroupId, null);
        var rowsForSomeoneElse = new List<GroupVisibility>
        {
            new() { AppUserId = "someone-else", GroupId = RootGroupId }
        };
        _groupVisibilityRepository.GroupVisibilityList(NoRowPlannerUserId).Returns(rowsForSomeoneElse);

        var result = await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        result.ShouldNotContain(NoRowPlannerUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsForGroupAsync_CalledTwiceForSameRoot_ResolvesVisibilityOnlyOnce()
    {
        var planner = MakeUser(ScopedPlannerUserId);
        SetupRoles(new List<AppUser>(), new List<AppUser> { planner });
        SetupGroup(RootGroupId, null);
        SetupVisibility(ScopedPlannerUserId, RootGroupId);

        await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);
        await _sut.GetPlanningUserIdsForGroupAsync(RootGroupId);

        await _groupVisibilityRepository.Received(1).GroupVisibilityList(ScopedPlannerUserId);
    }

    [Test]
    public async Task GetPlanningUserIdsAsync_CombinesAdminsAndAuthorised()
    {
        var admin = MakeUser(AdminUserId);
        var planner = MakeUser(ScopedPlannerUserId);
        SetupRoles(new List<AppUser> { admin }, new List<AppUser> { planner });

        var result = await _sut.GetPlanningUserIdsAsync();

        result.ShouldBe(new[] { AdminUserId, ScopedPlannerUserId }, ignoreOrder: true);
    }

    [Test]
    public async Task GetAdminUserIdsAsync_ReturnsOnlyAdmins()
    {
        var admin = MakeUser(AdminUserId);
        var planner = MakeUser(ScopedPlannerUserId);
        SetupRoles(new List<AppUser> { admin }, new List<AppUser> { planner });

        var result = await _sut.GetAdminUserIdsAsync();

        result.ShouldBe(new[] { AdminUserId });
    }
}
