// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the owner definition of "unstaffed": a regular shift needs Quantity x SumEmployees employees per day and is
/// unstaffed while fewer are engaged. Sporadic shifts (Quantity = days per period) and shifts covered by a container
/// template have no fixed daily demand and are never unstaffed. Values below one count as one.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class UnstaffedShiftPredicateTests
{
    private static ShiftDayAssignment Assignment(
        int quantity,
        int sumEmployees,
        int engaged,
        bool isSporadic = false,
        bool isInTemplateContainer = false) => new()
    {
        ShiftId = Guid.NewGuid(),
        Date = new DateOnly(2026, 10, 5),
        Quantity = quantity,
        SumEmployees = sumEmployees,
        Engaged = engaged,
        IsSporadic = isSporadic,
        IsInTemplateContainer = isInTemplateContainer,
    };

    [TestCase(1, 1, 0, true)]
    [TestCase(1, 1, 1, false)]
    [TestCase(2, 3, 5, true)]
    [TestCase(2, 3, 6, false)]
    [TestCase(2, 3, 7, false)]
    [TestCase(1, 3, 2, true)]
    [TestCase(3, 1, 3, false)]
    public void RegularShift_IsUnstaffed_WhileEngagedBelowQuantityTimesSumEmployees(
        int quantity, int sumEmployees, int engaged, bool expected)
    {
        UnstaffedShiftPredicate.IsUnstaffed(Assignment(quantity, sumEmployees, engaged)).ShouldBe(expected);
    }

    [Test]
    public void ConfigurationAlone_NeverDecides_EngagedIsRead()
    {
        UnstaffedShiftPredicate.IsUnstaffed(Assignment(quantity: 2, sumEmployees: 2, engaged: 0)).ShouldBeTrue();
        UnstaffedShiftPredicate.IsUnstaffed(Assignment(quantity: 2, sumEmployees: 2, engaged: 4)).ShouldBeFalse();
    }

    [Test]
    public void SporadicShift_IsNeverUnstaffed()
    {
        UnstaffedShiftPredicate.IsUnstaffed(Assignment(quantity: 3, sumEmployees: 2, engaged: 0, isSporadic: true))
            .ShouldBeFalse();
    }

    [Test]
    public void ShiftCoveredByContainerTemplate_IsNeverUnstaffed()
    {
        UnstaffedShiftPredicate.IsUnstaffed(Assignment(quantity: 1, sumEmployees: 1, engaged: 0, isInTemplateContainer: true))
            .ShouldBeFalse();
    }

    [TestCase(0, 1, 0, true)]
    [TestCase(1, 0, 0, true)]
    [TestCase(0, 0, 1, false)]
    public void ValuesBelowOne_CountAsOne(int quantity, int sumEmployees, int engaged, bool expected)
    {
        UnstaffedShiftPredicate.IsUnstaffed(Assignment(quantity, sumEmployees, engaged)).ShouldBe(expected);
    }
}
