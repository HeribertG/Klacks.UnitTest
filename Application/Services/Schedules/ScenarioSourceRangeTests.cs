// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the range of a scenario built on a source scenario: the requested range widened to the source range on
/// either side, and unchanged without a source.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public sealed class ScenarioSourceRangeTests
{
    private static readonly DateOnly From = new(2026, 10, 10);
    private static readonly DateOnly Until = new(2026, 10, 12);

    [Test]
    public void WithoutSource_KeepsTheRequestedRange()
        => ScenarioSourceRange.Widen(From, Until, null, null).ShouldBe((From, Until));

    [Test]
    public void WiderSource_WidensBothSides()
        => ScenarioSourceRange.Widen(From, Until, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))
            .ShouldBe((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));

    [Test]
    public void NarrowerSource_KeepsTheRequestedRange()
        => ScenarioSourceRange.Widen(From, Until, new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 11))
            .ShouldBe((From, Until));

    [Test]
    public void OverlappingSource_WidensOnlyTheSideItExceeds()
        => ScenarioSourceRange.Widen(From, Until, new DateOnly(2026, 10, 11), new DateOnly(2026, 10, 20))
            .ShouldBe((From, new DateOnly(2026, 10, 20)));
}
