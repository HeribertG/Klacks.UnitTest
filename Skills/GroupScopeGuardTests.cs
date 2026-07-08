// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupScopeGuard: background/system contexts and users without a configured
/// scope stay unrestricted; a scoped user is limited to the subtrees of their visible root
/// groups and gets an actionable error for out-of-scope groups.
/// </summary>

using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupScopeGuardTests
{
    private IUserManagementService _userManagementService = null!;
    private IGroupVisibilityRepository _groupVisibilityRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private GroupScopeGuard _guard = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SalesRootId = Guid.NewGuid();
    private static readonly Guid LogisticsRootId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _userManagementService = Substitute.For<IUserManagementService>();
        _groupVisibilityRepository = Substitute.For<IGroupVisibilityRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _guard = new GroupScopeGuard(_userManagementService, _groupVisibilityRepository, _groupRepository);
    }

    private static SkillExecutionContext Ctx(Guid? userId = null) => new()
    {
        UserId = userId ?? UserId,
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private AppUser SetupUser(bool isAdmin)
    {
        var user = new AppUser { Id = UserId.ToString() };
        _userManagementService.FindUserByIdAsync(UserId.ToString()).Returns(user);
        _userManagementService.IsUserInRoleAsync(user, Roles.Admin).Returns(isAdmin);
        return user;
    }

    private void SetupVisibilities(params Guid[] rootIds)
    {
        var rows = rootIds
            .Select(id => new GroupVisibility { AppUserId = UserId.ToString(), GroupId = id })
            .ToList();
        _groupVisibilityRepository.GroupVisibilityList(UserId.ToString())
            .Returns(rows);
        _groupRepository.GetRoots().Returns(new List<Group>
        {
            new() { Id = SalesRootId, Name = "Verkauf" },
            new() { Id = LogisticsRootId, Name = "Logistik" }
        });
    }

    [Test]
    public async Task EmptyUserId_BackgroundContext_IsUnrestricted()
    {
        var access = await _guard.GetAccessAsync(Ctx(Guid.Empty));

        Assert.That(access.IsUnrestricted, Is.True);
        await _userManagementService.DidNotReceive().FindUserByIdAsync(Arg.Any<string>());
    }

    [Test]
    public async Task UnknownUser_SystemPrincipal_IsUnrestricted()
    {
        _userManagementService.FindUserByIdAsync(UserId.ToString()).Returns((AppUser?)null);

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.True);
    }

    [Test]
    public async Task AdminUser_IsUnrestricted()
    {
        SetupUser(isAdmin: true);

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.True);
        await _groupVisibilityRepository.DidNotReceive().GroupVisibilityList(Arg.Any<string>());
    }

    [Test]
    public async Task UserWithoutVisibilityRows_KeepsLegacyUnrestrictedBehaviour()
    {
        SetupUser(isAdmin: false);
        SetupVisibilities();

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.True);
    }

    [Test]
    public async Task ScopedUser_SubgroupOfVisibleRoot_IsInScope()
    {
        SetupUser(isAdmin: false);
        SetupVisibilities(SalesRootId);
        var subgroup = new Group { Id = Guid.NewGuid(), Name = "Verkauf Nord", Root = SalesRootId };

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.False);
        Assert.That(access.IsInScope(subgroup), Is.True);
    }

    [Test]
    public async Task ScopedUser_VisibleRootItself_IsInScope()
    {
        SetupUser(isAdmin: false);
        SetupVisibilities(SalesRootId);
        var root = new Group { Id = SalesRootId, Name = "Verkauf", Root = null };

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsInScope(root), Is.True);
    }

    [Test]
    public async Task ScopedUser_GroupUnderForeignRoot_IsOutOfScope_AndFilteredOut()
    {
        SetupUser(isAdmin: false);
        SetupVisibilities(SalesRootId);
        var inScope = new Group { Id = Guid.NewGuid(), Name = "Verkauf Nord", Root = SalesRootId };
        var foreign = new Group { Id = Guid.NewGuid(), Name = "Logistik Ost", Root = LogisticsRootId };

        var access = await _guard.GetAccessAsync(Ctx());
        var filtered = access.Filter(new[] { inScope, foreign });

        Assert.That(access.IsInScope(foreign), Is.False);
        Assert.That(filtered, Is.EqualTo(new[] { inScope }));
    }

    [Test]
    public async Task ScopedUser_OutOfScopeError_NamesGroupAndVisibleRoots()
    {
        SetupUser(isAdmin: false);
        SetupVisibilities(SalesRootId);

        var access = await _guard.GetAccessAsync(Ctx());
        var message = access.BuildOutOfScopeError("Logistik Ost");

        Assert.That(message, Does.Contain("Group 'Logistik Ost' is outside your assigned group scope"));
        Assert.That(message, Does.Contain("Verkauf"));
    }

    [Test]
    public async Task ScopedUser_ForeignUsersVisibilityRows_DoNotWidenTheScope()
    {
        SetupUser(isAdmin: false);
        var rows = new List<GroupVisibility>
        {
            new() { AppUserId = UserId.ToString(), GroupId = SalesRootId },
            new() { AppUserId = Guid.NewGuid().ToString(), GroupId = LogisticsRootId }
        };
        _groupVisibilityRepository.GroupVisibilityList(UserId.ToString()).Returns(rows);
        _groupRepository.GetRoots().Returns(new List<Group>
        {
            new() { Id = SalesRootId, Name = "Verkauf" },
            new() { Id = LogisticsRootId, Name = "Logistik" }
        });

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsInScope(new Group { Id = LogisticsRootId, Name = "Logistik" }), Is.False);
        Assert.That(access.VisibleRootNames, Is.EqualTo(new[] { "Verkauf" }));
    }
}
