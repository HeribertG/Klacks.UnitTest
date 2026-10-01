// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The group write guard follows the group visibility scope: admins and callers without a user are
/// unrestricted, a restricted user may only write the visible groups (subgroups included through the scope),
/// and a non-admin without visibility rows may write no group at all (fail-closed).
/// </summary>

using Klacks.Api.Application.Services.Groups;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupVisibilityGuardTests
{
    private const string UserId = "supervisor-1";

    private IGroupVisibilityService _groupVisibilityService = null!;
    private IUserService _userService = null!;
    private GroupVisibilityGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _userService = Substitute.For<IUserService>();
        _userService.GetIdString().Returns(UserId);
        _guard = new GroupVisibilityGuard(_groupVisibilityService, _userService);
    }

    private void ScopeIs(GroupVisibilityScope scope)
    {
        _groupVisibilityService.GetVisibilityScopeAsync().Returns(Task.FromResult(scope));
    }

    [Test]
    public async Task Admin_IsUnrestricted_AndSeesEveryGroup()
    {
        ScopeIs(GroupVisibilityScope.Unrestricted());

        (await _guard.IsUnrestrictedAsync()).ShouldBeTrue();
        (await _guard.IsGroupVisibleAsync(Guid.NewGuid())).ShouldBeTrue();
        (await _guard.AreAllGroupsVisibleAsync([Guid.NewGuid(), Guid.NewGuid()])).ShouldBeTrue();
    }

    [Test]
    public async Task NoCallingUser_IsUnrestricted_WithoutAskingTheVisibilityService()
    {
        _userService.GetIdString().Returns((string?)null);
        ScopeIs(GroupVisibilityScope.Restricted([], []));

        (await _guard.IsUnrestrictedAsync()).ShouldBeTrue();
        (await _guard.IsGroupVisibleAsync(Guid.NewGuid())).ShouldBeTrue();
        await _groupVisibilityService.DidNotReceive().GetVisibilityScopeAsync();
    }

    [Test]
    public async Task EmptyUserId_IsTreatedLikeNoCallingUser()
    {
        _userService.GetIdString().Returns(string.Empty);
        ScopeIs(GroupVisibilityScope.Restricted([], []));

        (await _guard.IsGroupVisibleAsync(Guid.NewGuid())).ShouldBeTrue();
    }

    [Test]
    public async Task RestrictedUser_SeesOnlyTheGroupsOfTheScope()
    {
        var root = Guid.NewGuid();
        var subgroup = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        ScopeIs(GroupVisibilityScope.Restricted([root], [root, subgroup]));

        (await _guard.IsUnrestrictedAsync()).ShouldBeFalse();
        (await _guard.IsGroupVisibleAsync(root)).ShouldBeTrue();
        (await _guard.IsGroupVisibleAsync(subgroup)).ShouldBeTrue();
        (await _guard.IsGroupVisibleAsync(hidden)).ShouldBeFalse();
        (await _guard.AreAllGroupsVisibleAsync([root, subgroup])).ShouldBeTrue();
        (await _guard.AreAllGroupsVisibleAsync([root, hidden])).ShouldBeFalse();
    }

    [Test]
    public async Task RestrictedUserWithoutVisibilityRows_SeesNoGroup()
    {
        ScopeIs(GroupVisibilityScope.Restricted([], []));

        (await _guard.IsUnrestrictedAsync()).ShouldBeFalse();
        (await _guard.IsGroupVisibleAsync(Guid.NewGuid())).ShouldBeFalse();
    }

    [Test]
    public async Task EmptyCollection_IsTriviallyVisible()
    {
        ScopeIs(GroupVisibilityScope.Restricted([], []));

        (await _guard.AreAllGroupsVisibleAsync([])).ShouldBeTrue();
    }
}
