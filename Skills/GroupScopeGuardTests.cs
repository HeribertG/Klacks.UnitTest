// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupScopeGuard: it carries no rule of its own but follows IGroupVisibilityService for the
/// executing user (fail-closed). An execution without a user, an unknown user and a non-admin without
/// visibility rows see nothing; admins and installations without groups stay unrestricted; a scoped user is
/// limited to the subtrees of their visible root groups and gets an actionable error for out-of-scope groups.
/// </summary>

using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupScopeGuardTests
{
    private IGroupVisibilityService _groupVisibilityService = null!;
    private IGroupRepository _groupRepository = null!;
    private GroupScopeGuard _guard = null!;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SalesRootId = Guid.NewGuid();
    private static readonly Guid LogisticsRootId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.GetRoots().Returns(new List<Group>
        {
            new() { Id = SalesRootId, Name = "Verkauf" },
            new() { Id = LogisticsRootId, Name = "Logistik" }
        });
        _guard = new GroupScopeGuard(_groupVisibilityService, _groupRepository);
    }

    private static SkillExecutionContext Ctx(Guid? userId = null) => new()
    {
        UserId = userId ?? UserId,
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private void SetupScope(GroupVisibilityScope scope)
    {
        _groupVisibilityService
            .GetVisibilityScopeForUserAsync(UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(scope);
    }

    private void SetupVisibleRoots(params Guid[] rootIds)
    {
        SetupScope(GroupVisibilityScope.Restricted(rootIds, rootIds));
    }

    [Test]
    public async Task EmptyUserId_SeesNothing()
    {
        var access = await _guard.GetAccessAsync(Ctx(Guid.Empty));

        Assert.That(access.IsUnrestricted, Is.False);
        Assert.That(access.VisibleRootIds, Is.Empty);
        Assert.That(access.IsInScope(new Group { Id = SalesRootId, Name = "Verkauf" }), Is.False);
        await _groupVisibilityService.DidNotReceive()
            .GetVisibilityScopeForUserAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownUser_SeesNothing()
    {
        SetupScope(GroupVisibilityScope.Restricted([], []));

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.False);
        Assert.That(access.VisibleRootIds, Is.Empty);
    }

    [Test]
    public async Task AdminUser_IsUnrestricted()
    {
        SetupScope(GroupVisibilityScope.Unrestricted());

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.True);
        await _groupRepository.DidNotReceive().GetRoots();
    }

    [Test]
    public async Task UserWithoutVisibilityRows_SeesNothing()
    {
        SetupScope(GroupVisibilityScope.Restricted([], []));

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.False);
        Assert.That(access.Filter(new[] { new Group { Id = SalesRootId, Name = "Verkauf" } }), Is.Empty);
    }

    [Test]
    public async Task UserWithoutVisibilityRows_ErrorsSayThatNoGroupIsAssigned()
    {
        SetupScope(GroupVisibilityScope.Restricted([], []));

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.BuildOutOfScopeError("Verkauf"), Does.Contain("no group is assigned to you"));
        Assert.That(access.BuildRootCreationError(), Does.Contain("no group is assigned to you"));
    }

    [Test]
    public async Task ScopeIsResolvedForTheContextUser_NotTheAmbientCaller()
    {
        SetupVisibleRoots(SalesRootId);

        await _guard.GetAccessAsync(Ctx());

        await _groupVisibilityService.Received(1)
            .GetVisibilityScopeForUserAsync(UserId.ToString(), Arg.Any<CancellationToken>());
        await _groupVisibilityService.DidNotReceive().GetVisibilityScopeAsync();
    }

    [Test]
    public async Task ScopedUser_SubgroupOfVisibleRoot_IsInScope()
    {
        SetupVisibleRoots(SalesRootId);
        var subgroup = new Group { Id = Guid.NewGuid(), Name = "Verkauf Nord", Root = SalesRootId };

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsUnrestricted, Is.False);
        Assert.That(access.IsInScope(subgroup), Is.True);
    }

    [Test]
    public async Task ScopedUser_VisibleRootItself_IsInScope()
    {
        SetupVisibleRoots(SalesRootId);
        var root = new Group { Id = SalesRootId, Name = "Verkauf", Root = null };

        var access = await _guard.GetAccessAsync(Ctx());

        Assert.That(access.IsInScope(root), Is.True);
    }

    [Test]
    public async Task ScopedUser_GroupUnderForeignRoot_IsOutOfScope_AndFilteredOut()
    {
        SetupVisibleRoots(SalesRootId);
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
        SetupVisibleRoots(SalesRootId);

        var access = await _guard.GetAccessAsync(Ctx());
        var message = access.BuildOutOfScopeError("Logistik Ost");

        Assert.That(message, Does.Contain("Group 'Logistik Ost' is outside your assigned group scope"));
        Assert.That(message, Does.Contain("Verkauf"));
        Assert.That(access.VisibleRootNames, Is.EqualTo(new[] { "Verkauf" }));
    }
}
