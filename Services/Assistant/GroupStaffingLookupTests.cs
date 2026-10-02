// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins GroupStaffingLookup's ancestor rule for both root spellings found in the data: a root created by
/// GroupTreeService carries Root = null, migrated roots carry Root = their own id. In both cases a root is
/// staffed as soon as a descendant holds clients or shifts.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class GroupStaffingLookupTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void RootIsStaffed_WhenOnlyAChildHoldsMembers(bool rootCarriesNullRoot)
    {
        var rootId = Guid.NewGuid();
        var root = new Group { Id = rootId, Name = "Company", Root = rootCarriesNullRoot ? null : rootId, Lft = 1, Rgt = 4 };
        var child = new Group { Id = Guid.NewGuid(), Name = "Branch", Parent = rootId, Root = rootId, Lft = 2, Rgt = 3 };

        var lookup = GroupStaffingLookup.Build([root, child], [child.Id]);

        lookup.IsStaffed(child.Id).ShouldBeTrue();
        lookup.IsStaffed(root.Id).ShouldBeTrue();
    }

    [Test]
    public void AnotherRoot_IsNotStaffedByAForeignDescendant()
    {
        var rootId = Guid.NewGuid();
        var root = new Group { Id = rootId, Name = "Company", Root = null, Lft = 1, Rgt = 4 };
        var child = new Group { Id = Guid.NewGuid(), Name = "Branch", Parent = rootId, Root = rootId, Lft = 2, Rgt = 3 };
        var otherRoot = new Group { Id = Guid.NewGuid(), Name = "Other", Root = null, Lft = 5, Rgt = 6 };

        var lookup = GroupStaffingLookup.Build([root, child, otherRoot], [child.Id]);

        lookup.IsStaffed(otherRoot.Id).ShouldBeFalse();
    }
}
