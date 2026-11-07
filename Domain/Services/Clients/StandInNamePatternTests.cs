// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The name pattern of stand-ins created in bulk: exactly one index placeholder, the frame before and after it, and the
/// next free index above the highest one an existing name carries in the same frame.
/// </summary>

using Klacks.Api.Domain.Services.Clients;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Clients;

[TestFixture]
public class StandInNamePatternTests
{
    [TestCase("Fair {n}", true)]
    [TestCase("{n}", true)]
    [TestCase("Fair", false)]
    [TestCase("Fair {n} {n}", false)]
    [TestCase("Fair {N}", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void HasSingleIndexPlaceholder(string? pattern, bool expected)
    {
        StandInNamePattern.HasSingleIndexPlaceholder(pattern).ShouldBe(expected);
    }

    [Test]
    public void Expand_ReplacesThePlaceholder_KeepsTheFrame()
    {
        StandInNamePattern.Prefix("Fair {n} night").ShouldBe("Fair ");
        StandInNamePattern.Suffix("Fair {n} night").ShouldBe(" night");
        StandInNamePattern.Expand("Fair {n} night", 12).ShouldBe("Fair 12 night");
    }

    [Test]
    public void NextFreeIndex_WithoutMatchingNames_IsOne()
    {
        StandInNamePattern.NextFreeIndex("Fair {n}", []).ShouldBe(1L);
    }

    [Test]
    public void NextFreeIndex_IsOneAboveTheHighestIndexInTheFrame_IgnoringGapsAndOtherNames()
    {
        var names = new[] { "Fair 1", "Fair 7", "Fair 3", "Fair x", "Fair 2b", "Fair ", "Fair -4", "Fair 1 night", "Fairground 9" };

        StandInNamePattern.NextFreeIndex("Fair {n}", names).ShouldBe(8L);
    }

    [Test]
    public void NextFreeIndex_RespectsTheSuffix()
    {
        var names = new[] { "Fair 4 night", "Fair 9", "Fair 5 night" };

        StandInNamePattern.NextFreeIndex("Fair {n} night", names).ShouldBe(6L);
    }

    [Test]
    public void NextFreeIndex_AboveIntMax_IsALong_AndAnIndexBeyondIntIsIgnored()
    {
        StandInNamePattern.NextFreeIndex("F{n}", [$"F{int.MaxValue}"]).ShouldBe((long)int.MaxValue + 1);
        StandInNamePattern.NextFreeIndex("F{n}", ["F99999999999"]).ShouldBe(1L);
    }
}
