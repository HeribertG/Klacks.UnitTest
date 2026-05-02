// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreContractDayTests
{
    [Test]
    public void CoreContractDay_StoresPerDayContractSnapshot()
    {
        var day = new CoreContractDay(
            AgentId: "agent-007",
            Date: new DateOnly(2026, 4, 21),
            WorksOnDay: true,
            PerformsShiftWork: false,
            FullTimeShare: 0.5,
            MaximumHoursPerDay: 8,
            ContractId: Guid.Parse("99999999-9999-9999-9999-999999999999"));

        day.WorksOnDay.ShouldBeTrue();
        day.PerformsShiftWork.ShouldBeFalse();
        day.FullTimeShare.ShouldBe(0.5);
    }
}
