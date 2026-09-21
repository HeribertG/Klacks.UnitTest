// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// SkillPermissionGate is the shared answer to "may this account release this skill" for the approval
/// roster and the delegation handler. These tests pin its contract: rights are the EXPANSION of the
/// account's current roles, every required permission must be covered, an Admin passes regardless, an
/// unknown account holds nothing, and a skill without requirements is open to any existing account.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SkillPermissionGateTests
{
    private const string UserId = "user-1";
    private const string SkillOnlyPermission = "SkillOnly.Permission";

    private IUserManagementService _userManagementService = null!;
    private SkillPermissionGate _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _userManagementService = Substitute.For<IUserManagementService>();
        _sut = new SkillPermissionGate(_userManagementService);
    }

    private AppUser GivenUser(params string[] roles)
    {
        var user = new AppUser { Id = UserId, UserName = UserId };
        _userManagementService.FindUserByIdAsync(UserId).Returns(Task.FromResult<AppUser?>(user));
        _userManagementService.GetUserRolesAsync(user).Returns(Task.FromResult<IList<string>>(roles.ToList()));
        return user;
    }

    [Test]
    public async Task AuthorisedUser_HoldsAnExpandedRightOfTheirRole()
    {
        var user = GivenUser(Roles.Authorised);
        var anyAuthorisedRight = Permissions.ExpandRoles([Roles.Authorised]).First(right => right != Roles.Authorised);

        Assert.That(await _sut.HoldsAsync(user, [anyAuthorisedRight]), Is.True);
    }

    [Test]
    public async Task AuthorisedUser_DoesNotHoldASkillOnlyPermission()
    {
        var user = GivenUser(Roles.Authorised);

        Assert.That(await _sut.HoldsAsync(user, [SkillOnlyPermission]), Is.False);
    }

    /// <summary>
    /// Owner decision 2026-09-21: an Admin and a Supervisor may create groups, a caller without a role
    /// may not, and deleting one stays with the Admin. The gate is what decides it for every skill path,
    /// so the three cases are pinned here rather than only in the seed file.
    /// </summary>
    [Test]
    public async Task Supervisor_MayCreateGroups()
    {
        var user = GivenUser(Roles.Authorised);

        Assert.That(await _sut.HoldsAsync(user, [Permissions.CanCreateGroups]), Is.True);
    }

    [Test]
    public async Task Admin_MayCreateGroups()
    {
        var user = GivenUser(Roles.Admin);

        Assert.That(await _sut.HoldsAsync(user, [Permissions.CanCreateGroups]), Is.True);
    }

    [Test]
    public async Task CallerWithoutARole_MayNotCreateGroups()
    {
        var user = GivenUser();

        Assert.That(await _sut.HoldsAsync(user, [Permissions.CanCreateGroups]), Is.False);
    }

    [Test]
    public async Task Supervisor_MayNotReachASettingsGatedSkill()
    {
        var user = GivenUser(Roles.Authorised);

        Assert.That(await _sut.HoldsAsync(user, [Permissions.CanEditSettings]), Is.False);
    }

    [Test]
    public async Task Admin_PassesRegardlessOfTheRequiredPermissions()
    {
        var user = GivenUser(Roles.Admin);

        Assert.That(await _sut.HoldsAsync(user, [SkillOnlyPermission]), Is.True);
    }

    [Test]
    public async Task AllRequiredPermissionsMustBeCovered_OneMissingRefuses()
    {
        var user = GivenUser(Roles.Authorised);
        var anyAuthorisedRight = Permissions.ExpandRoles([Roles.Authorised]).First(right => right != Roles.Authorised);

        Assert.That(await _sut.HoldsAsync(user, [anyAuthorisedRight, SkillOnlyPermission]), Is.False);
    }

    [Test]
    public async Task NoRequiredPermissions_AnyExistingAccountPasses()
    {
        var user = GivenUser();

        Assert.That(await _sut.HoldsAsync(user, []), Is.True);
    }

    [Test]
    public async Task ByUserId_UnknownAccount_HoldsNothing()
    {
        _userManagementService.FindUserByIdAsync(UserId).Returns(Task.FromResult<AppUser?>(null));

        Assert.That(await _sut.HoldsAsync(UserId, []), Is.False);
    }

    [Test]
    public async Task ByUserId_KnownAccount_IsCheckedLikeTheAppUserOverload()
    {
        GivenUser(Roles.Admin);

        Assert.That(await _sut.HoldsAsync(UserId, [SkillOnlyPermission]), Is.True);
    }

    [Test]
    public async Task RolesAreReadOnEveryCall_NeverCached()
    {
        var user = GivenUser(Roles.Admin);
        await _sut.HoldsAsync(user, [SkillOnlyPermission]);
        _userManagementService.GetUserRolesAsync(user).Returns(Task.FromResult<IList<string>>([Roles.Authorised]));

        Assert.That(await _sut.HoldsAsync(user, [SkillOnlyPermission]), Is.False);
        await _userManagementService.Received(2).GetUserRolesAsync(user);
    }
}
