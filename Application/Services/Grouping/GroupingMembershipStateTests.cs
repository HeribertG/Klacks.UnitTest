// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the scope semantics of the membership state: members of a group and all its descendants are in
/// scope, virtual additions and removals change scopes immediately, and memberships pointing at unknown
/// groups, clients or shifts are ignored.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingMembershipStateTests
{
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();
    private static readonly Guid ClientA = Guid.NewGuid();
    private static readonly Guid ShiftA = Guid.NewGuid();

    private static readonly GroupingGroupTree Tree = new(
    [
        new GroupingGroupRecord(Parent, "Parent", null, null, null, null),
        new GroupingGroupRecord(Child, "Child", Parent, Parent, null, null),
    ]);

    private static GroupingMembershipState State(params GroupingMembershipRecord[] memberships) =>
        GroupingMembershipState.From(memberships, Tree, new HashSet<Guid> { ClientA }, new HashSet<Guid> { ShiftA });

    [Test]
    public void ScopeOfParent_IncludesMembersOfChild()
    {
        var state = State(
            new GroupingMembershipRecord(Guid.NewGuid(), Child, ClientA, null),
            new GroupingMembershipRecord(Guid.NewGuid(), Child, null, ShiftA));

        state.ScopeClients(Parent, Tree).ShouldContain(ClientA);
        state.ScopeShifts(Parent, Tree).ShouldContain(ShiftA);
        state.IsPlanningUnit(Parent).ShouldBeFalse();
        state.IsPlanningUnit(Child).ShouldBeTrue();
    }

    [Test]
    public void MembershipOfUnknownGroup_IsIgnored()
    {
        var state = State(new GroupingMembershipRecord(Guid.NewGuid(), Guid.NewGuid(), ClientA, null));

        state.GroupsOfClient(ClientA).ShouldBeEmpty();
    }

    [Test]
    public void AddAndRemove_ChangeScopeImmediately()
    {
        var state = State();
        state.AddClient(ClientA, Child);
        state.ScopeClients(Parent, Tree).ShouldContain(ClientA);

        state.RemoveClient(ClientA, Child);
        state.ScopeClients(Parent, Tree).ShouldNotContain(ClientA);
    }
}
