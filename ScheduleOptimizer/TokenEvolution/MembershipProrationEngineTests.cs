// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution;

/// <summary>
/// Planning effect of the membership proration (owner decision 2026-10-08): an employee who leaves on 15.03. keeps
/// his contract's monthly target in the unprorated run and Wizard 1 packs it into the 15 remaining member days; with
/// the target prorated to 15/31 he is not planned beyond his share. Both runs use the same deterministic config and
/// close the days after the exit; only the targets differ.
/// </summary>
[TestFixture]
public sealed class MembershipProrationEngineTests
{
    private const string Leaver = "LEAVER";
    private const double MonthlyTarget = 124;
    private const double ColleagueTarget = 160;
    private const double MaximumHours = 200;
    private const double ShiftHours = 8;
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly Until = new(2026, 3, 31);
    private static readonly DateOnly LastMemberDay = new(2026, 3, 15);

    private static readonly TokenEvolutionConfig Config = new()
    {
        PopulationSize = 20,
        MaxGenerations = 120,
        EarlyStopNoImprovementGenerations = 30,
        RandomSeed = 42,
        EvaluationParallelism = 1,
    };

    [Test]
    public void LeaverWithProratedTarget_IsNotPackedBeyondHisShare()
    {
        var factor = (double)MembershipTargetProration.FactorFor(new MembershipWindow(new DateOnly(2020, 1, 1), LastMemberDay), From, Until)!.Value;
        var proratedTarget = MonthlyTarget * factor;

        var prorated = LeaverHours(proratedTarget);
        var unprorated = LeaverHours(MonthlyTarget);

        TestContext.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"leaver hours: prorated run {prorated} (target {proratedTarget:0.0}), unprorated run {unprorated} (target {MonthlyTarget})"));

        prorated.ShouldBeLessThanOrEqualTo(
            proratedTarget + ShiftHours,
            "with the prorated target the leaver gets at most one shift above his share of the month");
        unprorated.ShouldBeGreaterThan(
            prorated,
            "the unprorated run packs more of the full monthly target into the 15 remaining days");
    }

    private static double LeaverHours(double leaverTarget)
    {
        var context = BuildContext(leaverTarget);
        var plan = TokenEvolutionLoop.Create().Run(context, Config);
        return plan.Tokens.Where(t => t.AgentId == Leaver).Sum(t => (double)t.TotalHours);
    }

    private static CoreWizardContext BuildContext(double leaverTarget)
    {
        var agents = new[]
        {
            Agent(Leaver, leaverTarget),
            Agent("COLLEAGUE_A", ColleagueTarget),
            Agent("COLLEAGUE_B", ColleagueTarget),
        };

        var shifts = new List<CoreShift>();
        var contractDays = new List<CoreContractDay>();
        for (var date = From; date <= Until; date = date.AddDays(1))
        {
            var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            shifts.Add(new CoreShift(Guid.NewGuid().ToString(), "EARLY", day, "06:00", "14:00", ShiftHours, 1, 0));
            foreach (var agent in agents)
            {
                var works = agent.Id != Leaver || date <= LastMemberDay;
                contractDays.Add(new CoreContractDay(agent.Id, date, works, true, 1, 10, Guid.Empty));
            }
        }

        return new CoreWizardContext
        {
            PeriodFrom = From,
            PeriodUntil = Until,
            Agents = agents,
            Shifts = shifts,
            ContractDays = contractDays,
            SchedulingMaxConsecutiveDays = 6,
        };
    }

    private static CoreAgent Agent(string id, double target) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: target,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = target,
        MaximumHours = MaximumHours,
        PerformsShiftWork = true,
        WorkOnMonday = true,
        WorkOnTuesday = true,
        WorkOnWednesday = true,
        WorkOnThursday = true,
        WorkOnFriday = true,
        WorkOnSaturday = true,
        WorkOnSunday = true,
    };
}
