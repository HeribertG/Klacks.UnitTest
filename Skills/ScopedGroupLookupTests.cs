// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScopedGroupLookup answers a group outside the caller's group scope exactly like a missing one (null), whether the
/// repository throws for the missing id like the real one or returns null like older stubs, and passes a group in scope through.
/// </summary>

using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScopedGroupLookupTests
{
    private IGroupRepository _groupRepository = null!;
    private Guid _visibleRoot;
    private Group _inScope = null!;
    private Group _outOfScope = null!;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _visibleRoot = Guid.NewGuid();
        _inScope = new Group { Id = Guid.NewGuid(), Name = "Verkauf Nord", Root = _visibleRoot };
        _outOfScope = new Group { Id = Guid.NewGuid(), Name = "Geheimprojekt", Root = Guid.NewGuid() };
        _groupRepository.Get(_inScope.Id).Returns(_inScope);
        _groupRepository.Get(_outOfScope.Id).Returns(_outOfScope);
    }

    private GroupScopeAccess Restricted() => GroupScopeAccess.Restricted(new[] { _visibleRoot }, new[] { "Verkauf" });

    [Test]
    public async Task GroupInScope_IsReturned()
    {
        var group = await ScopedGroupLookup.FindAsync(_groupRepository, Restricted(), _inScope.Id);

        group.ShouldBe(_inScope);
    }

    [Test]
    public async Task GroupOutsideScope_IsAnsweredLikeAMissingGroup()
    {
        var group = await ScopedGroupLookup.FindAsync(_groupRepository, Restricted(), _outOfScope.Id);

        group.ShouldBeNull();
    }

    [Test]
    public async Task MissingGroup_ThrownByTheRepository_IsNull()
    {
        var missingId = Guid.NewGuid();
        _groupRepository.Get(missingId)
            .Returns(Task.FromException<Group?>(new KeyNotFoundException($"Group with ID {missingId} not found.")));

        var group = await ScopedGroupLookup.FindAsync(_groupRepository, Restricted(), missingId);

        group.ShouldBeNull();
    }

    [Test]
    public async Task MissingGroup_ReturnedAsNull_IsNull()
    {
        var missingId = Guid.NewGuid();
        _groupRepository.Get(missingId).Returns((Group?)null);

        var group = await ScopedGroupLookup.FindAsync(_groupRepository, GroupScopeAccess.Unrestricted(), missingId);

        group.ShouldBeNull();
    }

    [Test]
    public async Task UnrestrictedScope_ReturnsAnyExistingGroup()
    {
        var group = await ScopedGroupLookup.FindAsync(_groupRepository, GroupScopeAccess.Unrestricted(), _outOfScope.Id);

        group.ShouldBe(_outOfScope);
    }
}
