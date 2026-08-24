// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Programs an IShiftGroupScopeReader substitute with a fixed entity-to-groups map, so a detector test
/// can state "this shift is in these groups" in one line. Both batched lookups answer from the same
/// map: a work id and a shift id are only ever different keys into the same shape of answer.
/// </summary>

using Klacks.Api.Domain.Interfaces.Schedules;

namespace Klacks.UnitTest.TestHelpers;

public static class ShiftGroupScopeReaderStub
{
    /// <param name="reader">The substitute to program.</param>
    /// <param name="rows">Entity id and the groups it belongs to; an entity left out resolves to no group.</param>
    public static void SetGroups(IShiftGroupScopeReader reader, params (Guid EntityId, Guid[] GroupIds)[] rows)
    {
        var map = (IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>)rows.ToDictionary(
            row => row.EntityId,
            row => (IReadOnlyList<Guid>)row.GroupIds.OrderBy(groupId => groupId).ToList());

        reader.GetGroupIdsByShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(map);
        reader.GetGroupIdsByWorkIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(map);
    }

    /// <summary>A substitute that resolves every entity to no group at all.</summary>
    public static IShiftGroupScopeReader WithoutAnyGroups()
    {
        var reader = Substitute.For<IShiftGroupScopeReader>();
        SetGroups(reader);
        return reader;
    }
}
