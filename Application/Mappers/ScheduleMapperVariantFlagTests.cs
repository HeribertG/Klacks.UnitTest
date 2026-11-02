// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that the variant marker of a shift-schedule row reaches the shift-schedule resource the Ui reads.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Mappers;

[TestFixture]
public class ScheduleMapperVariantFlagTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void ToShiftScheduleResource_CarriesTheVariantFlag(bool isVariantShift)
    {
        var mapper = new ScheduleMapper();

        var resource = mapper.ToShiftScheduleResource(new ShiftDayAssignment { IsVariantShift = isVariantShift });

        resource.IsVariantShift.ShouldBe(isVariantShift);
    }
}
