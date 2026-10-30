// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ContainerTemplateForDate is the one pick of the template of a date that the work expansion and assign_container_week
/// share: the ordinary template of the weekday, else a holiday variant of that weekday, else none.
/// </summary>

using Klacks.Api.Application.Services.Schedules;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ContainerTemplateForDateTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private static readonly DateOnly Sunday = new(2026, 11, 8);

    [Test]
    public void OrdinaryTemplate_WinsOverAHolidayVariantOfTheSameWeekday()
    {
        var holiday = new ContainerTemplate { Weekday = 1, IsHoliday = true };
        var ordinary = new ContainerTemplate { Weekday = 1, IsHoliday = false };

        ContainerTemplateForDate.Pick([holiday, ordinary], Monday).ShouldBeSameAs(ordinary);
    }

    [Test]
    public void HolidayVariant_IsUsedWhenItIsTheOnlyTemplateOfTheWeekday()
    {
        var holiday = new ContainerTemplate { Weekday = 1, IsHoliday = true };

        ContainerTemplateForDate.Pick([holiday], Monday).ShouldBeSameAs(holiday);
    }

    [Test]
    public void TemplateOfAnotherWeekday_IsNotPicked()
    {
        ContainerTemplateForDate.Pick([new ContainerTemplate { Weekday = 2 }], Monday).ShouldBeNull();
    }

    [Test]
    public void Sunday_IsStoredAsZero()
    {
        var sunday = new ContainerTemplate { Weekday = 0 };

        ContainerTemplateForDate.Pick([new ContainerTemplate { Weekday = 7 }, sunday], Sunday).ShouldBeSameAs(sunday);
    }
}
