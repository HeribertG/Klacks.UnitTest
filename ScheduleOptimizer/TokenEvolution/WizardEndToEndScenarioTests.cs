// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

/// <summary>
/// Diagnostic test: reproduces the E2E fixture (1 agent, 3 weekday shifts) in-memory to surface
/// the actual Stage-0 violations returned by the GA. Useful for pin-pointing fitness bugs.
/// </summary>
[TestFixture]
public class WizardEndToEndScenarioTests
{
    [Test]
    public void MinimalFixture_GaFinalScenario_ViolationBreakdown()
    {
        var agent = new CoreAgent(
            Id: "A",
            CurrentHours: 0,
            GuaranteedHours: 16,
            MaxConsecutiveDays: 6,
            MinRestHours: 11,
            Motivation: 0.5,
            MaxDailyHours: 10,
            MaxWeeklyHours: 50,
            MaxOptimalGap: 2)
        {
            FullTime = 40,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
        };

        var date1 = new DateOnly(2099, 1, 5);
        var date2 = new DateOnly(2099, 1, 6);
        var date3 = new DateOnly(2099, 1, 7);

        var shifts = new[]
        {
            new CoreShift("shift1", "FD", date1.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            new CoreShift("shift2", "FD", date2.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
            new CoreShift("shift3", "FD", date3.ToString("yyyy-MM-dd"), "08:00", "16:00", 8, 1, 0),
        };

        var contractDays = new[]
        {
            new CoreContractDay("A", date1, WorksOnDay: true, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.NewGuid()),
            new CoreContractDay("A", date2, WorksOnDay: true, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.NewGuid()),
            new CoreContractDay("A", date3, WorksOnDay: true, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.NewGuid()),
        };

        var context = new CoreWizardContext
        {
            PeriodFrom = date1,
            PeriodUntil = date3,
            Agents = [agent],
            Shifts = shifts,
            ContractDays = contractDays,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 11,
            SchedulingMaxOptimalGap = 2,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
        };

        var config = new TokenEvolutionConfig
        {
            PopulationSize = 20,
            MaxGenerations = 100,
            EarlyStopNoImprovementGenerations = 20,
            RandomSeed = 42,
        };

        var best = TokenEvolutionLoop.Create().Run(context, config);
        var violations = new TokenConstraintChecker().Check(best, context);

        TestContext.Out.WriteLine($"Final Stage 0 = {best.FitnessStage0}, Stage 1 = {best.FitnessStage1}, Tokens = {best.Tokens.Count}");
        foreach (var v in violations)
        {
            TestContext.Out.WriteLine($"  [{v.Kind}] agent={v.AgentId} date={v.Date} blockId={v.TokenBlockId} desc={v.Description}");
        }

        foreach (var token in best.Tokens)
        {
            TestContext.Out.WriteLine($"  token: date={token.Date} shiftTypeIndex={token.ShiftTypeIndex} start={token.StartAt:HH:mm} end={token.EndAt:HH:mm} block={token.BlockId}");
        }

        best.FitnessStage0.ShouldBe(0, "minimal fixture should converge to feasibility");
    }
}
