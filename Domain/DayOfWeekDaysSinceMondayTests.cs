// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins DayOfWeekExtensions.DaysSinceMonday: the Monday-based weekday offset that the week-start
/// calculations of the warm-start mapper and the skill-learning fitness service share.
/// </summary>

using Klacks.Api.Domain.Extensions;

namespace Klacks.UnitTest.Domain;

[TestFixture]
public class DayOfWeekDaysSinceMondayTests
{
    [TestCase(DayOfWeek.Monday, 0)]
    [TestCase(DayOfWeek.Tuesday, 1)]
    [TestCase(DayOfWeek.Wednesday, 2)]
    [TestCase(DayOfWeek.Thursday, 3)]
    [TestCase(DayOfWeek.Friday, 4)]
    [TestCase(DayOfWeek.Saturday, 5)]
    [TestCase(DayOfWeek.Sunday, 6)]
    public void DaysSinceMonday_CountsFromMondayWithSundayLast(DayOfWeek day, int expected)
    {
        day.DaysSinceMonday().ShouldBe(expected);
    }
}
