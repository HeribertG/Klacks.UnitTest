// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.ScheduleOptimizer.Common.RestDays;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Metrics;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Metrics;

/// <summary>
/// End-to-end proof of the weekly rest-day guarantee: the plans Wizard 1 produces (auction seed and
/// the full evolution loop) are fed into the schedule check (ClientTimeline +
/// ScheduleValidationBuilder.AddMinRestDays) and must not raise a single MinRestDays entry. Every week
/// touched by the period is judged, including the weeks crossing its edges, with the boundary works
/// of the context loaded into the timeline. The explicit October case is a synthetic twelve-person,
/// six-shift month with nights that prints the coverage the stricter rule leaves.
/// </summary>
[TestFixture]
public sealed class WeeklyRestDayEndToEndTests
{
    private const int AuctionSeed = 42;

    private const int LoopSeed = 11;

    private static readonly int[] OctoberSeeds = [11, 23, 37];

    private const string ClientName = "EndToEnd";

    private const double FullTimeHours = 160;

    private const double SlotHours = 8;

    private static readonly Dictionary<string, Func<CoreWizardContext>> Scenarios = new(StringComparer.Ordinal)
    {
        ["BernFiveFullTimeHomogeneous"] = WizardScenarioFixtures.BernFiveFullTimeHomogeneous,
        ["HeterogeneousMix"] = WizardScenarioFixtures.HeterogeneousMix,
        ["BoundaryWithPriorWorks"] = WizardScenarioFixtures.BoundaryWithPriorWorks,
    };

    [TestCase("BernFiveFullTimeHomogeneous")]
    [TestCase("HeterogeneousMix")]
    [TestCase("BoundaryWithPriorWorks")]
    public void AuctionPlan_IsNeverFlaggedByTheScheduleCheck(string scenarioName)
    {
        var context = Scenarios[scenarioName]();
        var auctioneer = new SlotAuctioneer(
            new FuzzyBiddingAgent(), new Stage0HardConstraintChecker(), new Stage1SoftConstraintChecker());

        var plan = auctioneer.Run(context, new Random(AuctionSeed)).Scenario;

        MinRestDayFindings(plan, context).ShouldBeEmpty();
    }

    [TestCase("BernFiveFullTimeHomogeneous")]
    [TestCase("HeterogeneousMix")]
    [TestCase("BoundaryWithPriorWorks")]
    public void EvolutionLoopPlan_IsNeverFlaggedByTheScheduleCheck(string scenarioName)
    {
        var context = Scenarios[scenarioName]();

        var plan = RunLoop(context, LoopSeed);

        MinRestDayFindings(plan, context).ShouldBeEmpty();
    }

    [Test, Explicit("Synthetic October month, 12 people and 6 shift types including nights; prints coverage per seed.")]
    public void SyntheticOctober_IsNeverFlaggedAndReportsCoverage()
    {
        var context = SyntheticOctober();
        foreach (var seed in OctoberSeeds)
        {
            var plan = RunLoop(context, seed);
            var metrics = WizardMetricsCalculator.Compute(plan, context, stage1EscalationCount: 0);
            var required = context.Shifts.Sum(shift => shift.RequiredAssignments);
            var planned = plan.Tokens.Count(token => !token.IsLocked);
            TestContext.Out.WriteLine(
                $"seed={seed} coverage={metrics.CoveragePercent:P1} planned={planned}/{required} open={required - planned} target={metrics.TargetReachedPercent:P1}");

            MinRestDayFindings(plan, context).ShouldBeEmpty();
        }
    }

    private static CoreScenario RunLoop(CoreWizardContext context, int seed)
        => TokenEvolutionLoop.Create().Run(context, new TokenEvolutionConfig
        {
            PopulationSize = 20,
            MaxGenerations = 50,
            EarlyStopNoImprovementGenerations = 15,
            RandomSeed = seed,
        });

    private static List<string> MinRestDayFindings(CoreScenario plan, CoreWizardContext context)
    {
        var findings = new List<string>();
        var checkFrom = CalendarWeekRestDays.WeekStartOf(context.PeriodFrom);
        var checkUntil = CalendarWeekRestDays.WeekStartOf(context.PeriodUntil)
            .AddDays(CalendarWeekRestDays.DaysPerWeek - 1);

        foreach (var agent in context.Agents)
        {
            var timeline = new ClientTimeline(Guid.NewGuid());
            foreach (var token in plan.Tokens.Where(token => token.AgentId == agent.Id))
            {
                timeline.AddBlock(Work(timeline.ClientId, token.StartAt, token.EndAt));
            }

            foreach (var work in context.BoundaryLockedWorks.Where(work => work.AgentId == agent.Id))
            {
                timeline.AddBlock(Work(timeline.ClientId, work.StartAt, work.EndAt));
            }

            foreach (var work in context.BoundaryExistingWorkBlockers.Where(work => work.AgentId == agent.Id))
            {
                timeline.AddBlock(Work(timeline.ClientId, work.StartAt, work.EndAt));
            }

            timeline.SortBlocks();
            var entries = new List<ScheduleValidationNotificationDto>();
            ScheduleValidationBuilder.AddMinRestDays(
                entries, timeline, ClientName, checkFrom, checkUntil, Policy(agent.MinRestDays, agent.MinRestHours > 0 ? agent.MinRestHours : context.SchedulingMinPauseHours));
            findings.AddRange(entries.Select(entry =>
                $"{agent.Id} week {entry.Date:yyyy-MM-dd}: {entry.CommentParams["actualDays"]} rest day(s)"));
        }

        return findings;
    }

    private static ScheduleBlock Work(Guid clientId, DateTime start, DateTime end)
        => new(Guid.NewGuid(), ScheduleBlockType.Work, clientId, start, end);

    private static SchedulingPolicy Policy(int minRestDays, double minRestHours) => new(
        MinRestHours: TimeSpan.FromHours(minRestHours),
        MaxDailyHours: TimeSpan.FromHours(10),
        MaxConsecutiveDays: 6,
        MaxWeeklyHours: TimeSpan.FromHours(50),
        MinRestDays: minRestDays);

    private static CoreWizardContext SyntheticOctober()
    {
        var from = new DateOnly(2026, 10, 1);
        var until = new DateOnly(2026, 10, 31);
        var shiftDefinitions = new (Guid Id, string Name, string Start, string End)[]
        {
            (new Guid("00000000-0000-0000-0000-000000000401"), "Early1", "06:00", "14:00"),
            (new Guid("00000000-0000-0000-0000-000000000402"), "Early2", "07:00", "15:00"),
            (new Guid("00000000-0000-0000-0000-000000000403"), "Middle", "09:00", "17:00"),
            (new Guid("00000000-0000-0000-0000-000000000404"), "Late1", "14:00", "22:00"),
            (new Guid("00000000-0000-0000-0000-000000000405"), "Late2", "15:00", "23:00"),
            (new Guid("00000000-0000-0000-0000-000000000406"), "Night", "22:00", "06:00"),
        };

        var shifts = new List<CoreShift>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            var iso = date.ToString("yyyy-MM-dd");
            shifts.AddRange(shiftDefinitions.Select(definition => new CoreShift(
                definition.Id.ToString(), definition.Name, iso, definition.Start, definition.End, SlotHours, 1, 0)));
        }

        var agents = Enumerable.Range(1, 12).Select(index => OctoberAgent($"P{index:D2}")).ToList();

        return new CoreWizardContext
        {
            PeriodFrom = from,
            PeriodUntil = until,
            Agents = agents,
            Shifts = shifts,
            SchedulingMaxConsecutiveDays = 6,
            SchedulingMaxDailyHours = 10,
            SchedulingMaxWeeklyHours = 50,
            SchedulingMinPauseHours = 11,
        };
    }

    private static CoreAgent OctoberAgent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: FullTimeHours,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2)
    {
        FullTime = FullTimeHours,
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
}
