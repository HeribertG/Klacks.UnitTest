// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Models;

[TestFixture]
public class CoreWizardContextTests
{
    [Test]
    public void CoreWizardContext_AggregatesAllInputs()
    {
        var ctx = new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 4, 21),
            PeriodUntil = new DateOnly(2026, 5, 21),
            Agents = [],
            Shifts = [],
            ContractDays = [],
            ScheduleCommands = [],
            ShiftPreferences = [],
            BreakBlockers = [],
            LockedWorks = [],
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
            SchedulingMaxOptimalGap = 2,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
            AnalyseToken = null,
        };

        ctx.PeriodFrom.ShouldBe(new DateOnly(2026, 4, 21));
        ctx.PeriodUntil.ShouldBe(new DateOnly(2026, 5, 21));
        ctx.AnalyseToken.ShouldBeNull();
    }

    [Test]
    public void CoreWizardContext_AnalyseTokenPropagates()
    {
        var token = Guid.NewGuid();
        var ctx = new CoreWizardContext
        {
            PeriodFrom = new DateOnly(2026, 4, 21),
            PeriodUntil = new DateOnly(2026, 5, 21),
            AnalyseToken = token,
        };

        ctx.AnalyseToken.ShouldBe(token);
    }
}
