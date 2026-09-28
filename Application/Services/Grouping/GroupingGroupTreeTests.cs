// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that the in-memory group tree resolves subtrees and ancestors over Parent exactly like the plan
/// view (recursive parent walk), survives cycles and treats a missing parent as a root.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingGroupTreeTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();
    private static readonly Guid GrandChild = Guid.NewGuid();
    private static readonly Guid Orphan = Guid.NewGuid();

    private static GroupingGroupTree Tree() => new(
    [
        new GroupingGroupRecord(Root, "Root", null, null, null, null),
        new GroupingGroupRecord(Child, "Child", Root, Root, null, null),
        new GroupingGroupRecord(GrandChild, "GrandChild", Child, Root, null, null),
        new GroupingGroupRecord(Orphan, "Orphan", Guid.NewGuid(), null, null, null),
    ]);

    [Test]
    public void SelfAndDescendants_ContainsWholeSubtree()
    {
        Tree().SelfAndDescendants(Root).ShouldBe(new[] { Root, Child, GrandChild }, ignoreOrder: true);
    }

    [Test]
    public void SelfAndAncestors_WalksUpToTheRoot()
    {
        Tree().SelfAndAncestors(GrandChild).ShouldBe(new[] { GrandChild, Child, Root });
    }

    [Test]
    public void MissingParent_IsTreatedAsRoot()
    {
        Tree().SelfAndAncestors(Orphan).ShouldBe(new[] { Orphan });
    }

    [Test]
    public void Cycle_DoesNotLoop()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var tree = new GroupingGroupTree(
        [
            new GroupingGroupRecord(a, "A", b, null, null, null),
            new GroupingGroupRecord(b, "B", a, null, null, null),
        ]);

        tree.SelfAndDescendants(a).ShouldBe(new[] { a, b }, ignoreOrder: true);
        tree.SelfAndAncestors(a).ShouldBe(new[] { a, b });
    }

    [Test]
    public void WithGroup_AddsAVirtualRootWithoutChangingTheOriginal()
    {
        var original = Tree();
        var extended = original.WithGroup(new GroupingGroupRecord(Guid.NewGuid(), "New", null, null, null, null));

        extended.GroupIds.Count.ShouldBe(original.GroupIds.Count + 1);
    }
}
