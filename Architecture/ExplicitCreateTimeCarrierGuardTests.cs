// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard pinning the set of entities that implement IKeepsExplicitCreateTime. The context stamps
/// every added entity with the time of the save, except a carrier of this marker whose CreateTime is set: that
/// value survives the insert. Such a value orders the rows of a conversation and drives the retention window, so a
/// new carrier is a decision to be made on purpose, not a side effect of adding an interface.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ExplicitCreateTimeCarrierGuardTests
{
    [Test]
    public void TheEntitiesThatKeepAnExplicitCreateTime_AreExactlyTheKnownOnes()
    {
        var carriers = typeof(IKeepsExplicitCreateTime).Assembly
            .GetTypes()
            .Where(type => !type.IsInterface && typeof(IKeepsExplicitCreateTime).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToList();

        carriers.ShouldBe(
            [typeof(LLMMessage)],
            $"Every carrier of {nameof(IKeepsExplicitCreateTime)} keeps a CreateTime that is set instead of having it stamped " +
            "with the time of the save. That changes the order of the rows and the retention window that counts from " +
            "CreateTime, so a new carrier must be reviewed for both and then added to this list. Found: " +
            string.Join(", ", carriers.Select(type => type.Name)));
    }
}
