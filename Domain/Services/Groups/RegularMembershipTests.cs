// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// RegularMembership is the one rule the master data reads memberships by: no scenario token and not temporary. The
/// expression, the test on a loaded row and the filtered navigations built from the expression must agree, so the
/// navigations are compiled and run over the same rows here; that they translate to SQL is pinned by
/// TemporaryMembershipQueriesNpgsqlTranslationTests.
/// </summary>

using Klacks.Api.Domain.Services.Groups;

namespace Klacks.UnitTest.Domain.Services.Groups;

[TestFixture]
public class RegularMembershipTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    public void OnlyARowWithoutTokenThatIsNotTemporary_IsRegular(bool hasToken, bool isTemporary, bool expected)
    {
        // Arrange
        var item = Membership(hasToken, isTemporary);

        // Act
        var held = RegularMembership.Holds(item);
        var matched = RegularMembership.IsRegular.Compile()(item);

        // Assert
        held.ShouldBe(expected);
        matched.ShouldBe(expected);
    }

    [Test]
    public void OfClient_KeepsOnlyTheRegularMembershipsOfTheClient()
    {
        // Arrange
        var regular = Membership(hasToken: false, isTemporary: false);
        var client = new Client
        {
            Id = ClientId,
            GroupItems =
            [
                regular,
                Membership(hasToken: false, isTemporary: true),
                Membership(hasToken: true, isTemporary: true),
                Membership(hasToken: true, isTemporary: false),
            ],
        };

        // Act
        var kept = RegularMembership.OfClient.Compile()(client).ToList();

        // Assert
        kept.ShouldHaveSingleItem().ShouldBeSameAs(regular);
    }

    [Test]
    public void MembersOfGroup_KeepsOnlyTheRegularClientRows_AndLeavesTheShiftLinksOut()
    {
        // Arrange
        var regular = Membership(hasToken: false, isTemporary: false);
        var group = new Group
        {
            Id = GroupId,
            GroupItems =
            [
                regular,
                Membership(hasToken: false, isTemporary: true),
                Membership(hasToken: true, isTemporary: true),
                new GroupItem { Id = Guid.NewGuid(), GroupId = GroupId, ShiftId = Guid.NewGuid() },
                new GroupItem { Id = Guid.NewGuid(), GroupId = GroupId, ClientId = ClientId, ShiftId = Guid.NewGuid() },
            ],
        };

        // Act
        var kept = RegularMembership.MembersOfGroup.Compile()(group).ToList();

        // Assert
        kept.ShouldHaveSingleItem().ShouldBeSameAs(regular);
    }

    [Test]
    public void ItemsOfGroup_KeepsTheRegularMembershipsAndTheRealShiftLinks()
    {
        // Arrange
        var regular = Membership(hasToken: false, isTemporary: false);
        var shiftLink = new GroupItem { Id = Guid.NewGuid(), GroupId = GroupId, ShiftId = Guid.NewGuid() };
        var group = new Group
        {
            Id = GroupId,
            GroupItems =
            [
                regular,
                shiftLink,
                Membership(hasToken: false, isTemporary: true),
                Membership(hasToken: true, isTemporary: true),
                new GroupItem { Id = Guid.NewGuid(), GroupId = GroupId, ShiftId = Guid.NewGuid(), AnalyseToken = Guid.NewGuid() },
            ],
        };

        // Act
        var kept = RegularMembership.ItemsOfGroup.Compile()(group).ToList();

        // Assert
        kept.ShouldBe(new[] { regular, shiftLink });
    }

    [Test]
    public void ClientHasOne_IsTrueOnlyForARegularMembershipThatFulfilsTheCondition()
    {
        // Arrange
        var otherGroup = Guid.NewGuid();
        var member = new Client { Id = ClientId, GroupItems = [Membership(hasToken: false, isTemporary: false)] };
        var guest = new Client { Id = Guid.NewGuid(), GroupItems = [Membership(hasToken: false, isTemporary: true)] };
        var borrowedInAScenario = new Client { Id = Guid.NewGuid(), GroupItems = [Membership(hasToken: true, isTemporary: true)] };
        var inGroup = RegularMembership.ClientHasOne(item => item.GroupId == GroupId).Compile();
        var inOtherGroup = RegularMembership.ClientHasOne(item => item.GroupId == otherGroup).Compile();

        // Act and assert
        inGroup(member).ShouldBeTrue();
        inGroup(guest).ShouldBeFalse();
        inGroup(borrowedInAScenario).ShouldBeFalse();
        inOtherGroup(member).ShouldBeFalse();
    }

    private static GroupItem Membership(bool hasToken, bool isTemporary) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        GroupId = GroupId,
        AnalyseToken = hasToken ? Guid.NewGuid() : null,
        IsTemporary = isTemporary,
    };
}
