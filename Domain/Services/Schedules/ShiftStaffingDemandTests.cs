// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the single daily-demand formula shared by the wizard, the grouping capacity check, the unstaffed predicate
/// and the coverage statistics: Quantity (shifts per day) x SumEmployees (employees per shift), values below one
/// counting as one; sporadic and container-template-covered shift days carry no fixed daily demand.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ShiftStaffingDemandTests
{
    [TestCase(1, 1, 1)]
    [TestCase(3, 1, 3)]
    [TestCase(1, 4, 4)]
    [TestCase(2, 3, 6)]
    [TestCase(0, 3, 3)]
    [TestCase(2, 0, 2)]
    [TestCase(-1, -1, 1)]
    public void PerDay_IsQuantityTimesSumEmployees(int quantity, int sumEmployees, int expected)
    {
        ShiftStaffingDemand.PerDay(quantity, sumEmployees).ShouldBe(expected);
    }

    [Test]
    public void RequiredOn_RegularShiftDay_IsPerDayDemand()
    {
        var day = new ShiftDayAssignment { Quantity = 2, SumEmployees = 3 };

        ShiftStaffingDemand.RequiredOn(day).ShouldBe(6);
    }

    [Test]
    public void RequiredOn_SporadicShiftDay_IsZero()
    {
        var day = new ShiftDayAssignment { Quantity = 2, SumEmployees = 3, IsSporadic = true };

        ShiftStaffingDemand.RequiredOn(day).ShouldBe(0);
    }

    [Test]
    public void RequiredOn_ShiftDayCoveredByContainerTemplate_IsZero()
    {
        var day = new ShiftDayAssignment { Quantity = 1, SumEmployees = 1, IsInTemplateContainer = true };

        ShiftStaffingDemand.RequiredOn(day).ShouldBe(0);
    }
}
