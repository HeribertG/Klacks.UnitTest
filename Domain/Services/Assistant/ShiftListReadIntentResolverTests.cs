// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ShiftListReadIntentResolver: the shift list page guarantees search_shifts, any other page (including pages below the
/// list, the schedule and a missing route) guarantees nothing. Query strings, fragments and a trailing slash do not matter.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ShiftListReadIntentResolverTests
{
    [TestCase("/workplace/shift")]
    [TestCase("/workplace/shift/")]
    [TestCase("/WORKPLACE/Shift")]
    [TestCase("/workplace/shift?filter=bern")]
    [TestCase("/workplace/shift#top")]
    public void TheShiftListPage_GuaranteesSearchShifts(string route)
    {
        ShiftListReadIntentResolver.IsShiftListPage(route).ShouldBeTrue();
        ShiftListReadIntentResolver.GuaranteedSkillNames(route)
            .ShouldBe([ShiftListReadIntentResolver.SearchShiftsSkillName]);
    }

    [TestCase("/workplace/schedule")]
    [TestCase("/workplace/shift/edit-shift/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [TestCase("/workplace/shifts")]
    [TestCase("/workplace/dashboard")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void AnyOtherRoute_GuaranteesNothing(string? route)
    {
        ShiftListReadIntentResolver.IsShiftListPage(route).ShouldBeFalse();
        ShiftListReadIntentResolver.GuaranteedSkillNames(route).ShouldBeEmpty();
    }
}
