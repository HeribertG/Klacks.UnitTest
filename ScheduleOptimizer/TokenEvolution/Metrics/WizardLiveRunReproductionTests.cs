// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// Reproduces the live run of 2026-06-12 (group BE/Bern: F10/S10/N10 over June, 3 agents)
/// where only F10 weekday slots were planned and 68 slots stayed unfillable.
/// </summary>
[TestFixture]
public sealed class WizardLiveRunReproductionTests
{
    private static CoreAgent MakeBernAgent(string id, double guaranteed) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: guaranteed,
        MaxConsecutiveDays: 6,
        MinRestHours: 12,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = guaranteed,
        MaxWorkDays = 5,
        MinRestDays = 2,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };

    private static CoreWizardContext BuildBernJuneContext()
    {
        var from = new DateOnly(2026, 6, 1);
        var until = new DateOnly(2026, 6, 30);
        var f10 = Guid.NewGuid().ToString();
        var s10 = Guid.NewGuid().ToString();
        var n10 = Guid.NewGuid().ToString();

        var shifts = new List<CoreShift>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            var d = date.ToString("yyyy-MM-dd");
            shifts.Add(new CoreShift(f10, "F10", d, "07:00", "15:00", 8, 1, 0));
            shifts.Add(new CoreShift(s10, "S10", d, "15:00", "23:00", 8, 1, 0));
            shifts.Add(new CoreShift(n10, "N10", d, "23:00", "07:00", 8, 1, 0));
        }

        return new CoreWizardContext
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = new[]
            {
                MakeBernAgent("Heribert", 180),
                MakeBernAgent("Tommaso", 180),
                MakeBernAgent("Frey", 160),
            },
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 12,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
        };
    }

    [Test, Explicit("Diagnostic — mirrors the live mid-period contract starts (Frey/Marie-Anne from 06-06).")]
    public void Diagnose_BernJune_MidPeriodContracts_FirstWeekMustBePlanned()
    {
        var from = new DateOnly(2026, 6, 1);
        var until = new DateOnly(2026, 6, 30);
        var lateStart = new DateOnly(2026, 6, 6);

        var heribert = MakeBernAgent("Heribert", 180);
        var frey = MakeBernAgent("Frey", 160) with { MaximumHours = 200 };
        var marieAnne = MakeBernAgent("MarieAnne", 0) with { MaximumHours = 75, FullTime = 180 };
        heribert = heribert with { MaximumHours = 200 };

        var contractDays = new List<CoreContractDay>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            contractDays.Add(new CoreContractDay("Heribert", date, WorksOnDay: true, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.Empty));
            var lateActive = date >= lateStart;
            contractDays.Add(new CoreContractDay("Frey", date, WorksOnDay: lateActive, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.Empty));
            contractDays.Add(new CoreContractDay("MarieAnne", date, WorksOnDay: lateActive, PerformsShiftWork: true, FullTimeShare: 1, MaximumHoursPerDay: 10, ContractId: Guid.Empty));
        }

        var baseCtx = BuildBernJuneContext();
        var ctx = new CoreWizardContext
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = new[] { heribert, frey, marieAnne },
            Shifts = baseCtx.Shifts,
            ContractDays = contractDays,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMinPauseHours = 12,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
        };

        var loop = TokenEvolutionLoop.Create();
        var config = new TokenEvolutionConfig { PopulationSize = 50, MaxGenerations = 200, RandomSeed = 42 };
        var best = loop.Run(ctx, config);

        var firstWeekTokens = best.Tokens.Where(t => t.Date < lateStart).ToList();
        TestContext.Out.WriteLine(
            $"RESULT tokens={best.Tokens.Count}/90 hardViol={best.FitnessStage0} firstFiveDays={firstWeekTokens.Count}");
        foreach (var token in firstWeekTokens.OrderBy(t => t.Date))
        {
            TestContext.Out.WriteLine($"  {token.Date} type={token.ShiftTypeIndex} agent={token.AgentId}");
        }
        foreach (var agent in ctx.Agents)
        {
            var agentTokens = best.Tokens.Where(t => t.AgentId == agent.Id).ToList();
            var hours = agentTokens.Sum(t => (double)t.TotalHours);
            var early = agentTokens.Count(t => t.ShiftTypeIndex == 0);
            var late = agentTokens.Count(t => t.ShiftTypeIndex == 1);
            var night = agentTokens.Count(t => t.ShiftTypeIndex == 2);
            TestContext.Out.WriteLine(
                $"  {agent.Id,-9} hours={hours,6:F1} target={agent.GuaranteedHours} max={agent.MaximumHours} types E/L/N={early}/{late}/{night}");
        }

        firstWeekTokens.ShouldNotBeEmpty("Heribert has an active contract from day one — the first five days must not stay empty");
    }

    [Test, Explicit("Diagnostic — prints per-shift-type validity and full GA outcome for the live-run setup.")]
    public void Diagnose_BernJune_WhyOnlyEarlyShifts()
    {
        var ctx = BuildBernJuneContext();

        var emptyTokens = new List<CoreToken>();
        foreach (var slot in ctx.Shifts.Take(9))
        {
            var start = TimeOnly.Parse(slot.StartTime);
            var end = TimeOnly.Parse(slot.EndTime);
            var date = DateOnly.Parse(slot.Date);
            var startUtc = date.ToDateTime(start);
            var endUtc = end <= start ? date.AddDays(1).ToDateTime(end) : date.ToDateTime(end);
            var typeIndex = ShiftTypeInference.FromStartTime(start);

            foreach (var agent in ctx.Agents)
            {
                var slotRef = Guid.TryParse(slot.Id, out var parsedRef) ? parsedRef : Guid.Empty;
                var valid = SlotConstraintFilter.IsValidAssignment(
                    agent, date, typeIndex, slotRef, (decimal)slot.Hours, ctx, emptyTokens, startUtc, endUtc);
                TestContext.Out.WriteLine(
                    $"slot={slot.Id[..8]} {slot.Date} {slot.StartTime}-{slot.EndTime} type={typeIndex} agent={agent.Id,-9} validOnEmptyPlan={valid}");
            }
        }

        var loop = TokenEvolutionLoop.Create();
        var config = new TokenEvolutionConfig { PopulationSize = 50, MaxGenerations = 200, RandomSeed = 42 };
        var best = loop.Run(ctx, config);

        var byType = best.Tokens
            .GroupBy(t => t.ShiftTypeIndex)
            .ToDictionary(g => g.Key, g => g.Count());
        TestContext.Out.WriteLine(
            $"RESULT tokens={best.Tokens.Count}/90 hardViol={best.FitnessStage0} early={byType.GetValueOrDefault(0)} late={byType.GetValueOrDefault(1)} night={byType.GetValueOrDefault(2)}");
        foreach (var agent in ctx.Agents)
        {
            var hours = best.Tokens.Where(t => t.AgentId == agent.Id).Sum(t => (double)t.TotalHours);
            TestContext.Out.WriteLine($"  {agent.Id,-9} hours={hours,6:F1} target={agent.GuaranteedHours}");
        }

        best.Tokens.ShouldNotBeEmpty();
    }
}
