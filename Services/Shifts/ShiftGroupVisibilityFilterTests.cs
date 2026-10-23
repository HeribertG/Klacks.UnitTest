// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that the two forms of the shift group-visibility rule cannot drift apart: the expression the database query
/// uses (ShiftGroupVisibilityFilter.VisibleTo, applied by ShiftGroupVisibilityQuery) and the in-memory check
/// Application uses (IsVisible) agree for a shift without group, with only a deleted group, with a visible group, with
/// a foreign group and with a mix, under the restrictions null (unrestricted), empty (fail-closed) and set.
/// </summary>

using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Shouldly;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftGroupVisibilityFilterTests
{
    private const string NoGroup = "no group";
    private const string OnlyDeletedGroup = "only deleted group";
    private const string VisibleGroup = "visible group";
    private const string ForeignGroup = "foreign group";
    private const string Mixed = "foreign and visible group";
    private const string Unrestricted = "null";
    private const string Empty = "empty";
    private const string Restricted = "visible group only";

    private static readonly Guid VisibleGroupId = Guid.NewGuid();
    private static readonly Guid ForeignGroupId = Guid.NewGuid();

    [TestCase(NoGroup, Unrestricted, true)]
    [TestCase(OnlyDeletedGroup, Unrestricted, true)]
    [TestCase(VisibleGroup, Unrestricted, true)]
    [TestCase(ForeignGroup, Unrestricted, true)]
    [TestCase(Mixed, Unrestricted, true)]
    [TestCase(NoGroup, Empty, true)]
    [TestCase(OnlyDeletedGroup, Empty, true)]
    [TestCase(VisibleGroup, Empty, false)]
    [TestCase(ForeignGroup, Empty, false)]
    [TestCase(Mixed, Empty, false)]
    [TestCase(NoGroup, Restricted, true)]
    [TestCase(OnlyDeletedGroup, Restricted, true)]
    [TestCase(VisibleGroup, Restricted, true)]
    [TestCase(ForeignGroup, Restricted, false)]
    [TestCase(Mixed, Restricted, true)]
    public void QueryRuleAndInMemoryRule_Agree(string shiftKind, string restrictionKind, bool expectedVisible)
    {
        var shift = CreateShift(shiftKind);
        var restriction = CreateRestriction(restrictionKind);

        var inMemory = ShiftGroupVisibilityFilter.IsVisible(shift, restriction);
        var query = ShiftGroupVisibilityQuery.Apply(new[] { shift }.AsQueryable(), restriction).Any();

        inMemory.ShouldBe(expectedVisible);
        query.ShouldBe(expectedVisible);
        if (restriction != null)
        {
            ShiftGroupVisibilityFilter.VisibleTo(restriction).Compile()(shift).ShouldBe(expectedVisible);
        }
    }

    private static Shift CreateShift(string kind)
    {
        var shift = new Shift { Id = Guid.NewGuid() };
        switch (kind)
        {
            case OnlyDeletedGroup:
                shift.GroupItems.Add(new GroupItem { GroupId = VisibleGroupId, IsDeleted = true });
                break;
            case VisibleGroup:
                shift.GroupItems.Add(new GroupItem { GroupId = VisibleGroupId });
                break;
            case ForeignGroup:
                shift.GroupItems.Add(new GroupItem { GroupId = ForeignGroupId });
                break;
            case Mixed:
                shift.GroupItems.Add(new GroupItem { GroupId = ForeignGroupId });
                shift.GroupItems.Add(new GroupItem { GroupId = VisibleGroupId });
                break;
        }

        return shift;
    }

    private static IReadOnlyCollection<Guid>? CreateRestriction(string kind)
    {
        return kind switch
        {
            Unrestricted => null,
            Empty => [],
            _ => [VisibleGroupId],
        };
    }
}
