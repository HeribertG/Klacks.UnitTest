// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.TokenEvolution.Initialization;

/// <summary>
/// The canon template is derived from the context: forward rotation early -> late -> night, blocks of the agents' soft
/// block length, night blocks short enough that the morning after still fits the consecutive-day cap, rest gaps of at
/// least MinRestDays, and evenly spread offsets so every agent follows the same rhythm shifted.
/// </summary>
[TestFixture]
public sealed class RotationTemplateTests
{
    private static List<(int Kind, int Length)> Blocks(IReadOnlyList<int> pattern)
    {
        var blocks = new List<(int, int)>();
        for (var i = 0; i < pattern.Count;)
        {
            var j = i;
            while (j < pattern.Count && pattern[j] == pattern[i])
            {
                j++;
            }

            blocks.Add((pattern[i], j - i));
            i = j;
        }

        return blocks;
    }

    [Test]
    public void Scenario4_RotatesForwardWithBlocksAndRestGaps()
    {
        var template = RotationTemplate.Build(RotationTestContexts.Scenario4())!;

        var blocks = Blocks(template.Pattern);
        var work = blocks.Where(b => b.Kind != RotationTemplate.FreeDay).ToList();
        work.Select(b => b.Kind).ShouldBe(new[] { 0, 1, 2 });
        work.ShouldAllBe(b => b.Length <= 5);
        work.Single(b => b.Kind == 2).Length.ShouldBeLessThanOrEqualTo(5);
        blocks.Where(b => b.Kind == RotationTemplate.FreeDay).ShouldAllBe(b => b.Length >= 2);
    }

    [Test]
    public void WorkRatio_MatchesDemandPerAgent()
    {
        var template = RotationTemplate.Build(RotationTestContexts.Scenario4())!;

        var ratio = template.Pattern.Count(p => p != RotationTemplate.FreeDay) / (double)template.Pattern.Count;

        ratio.ShouldBe(9d / 15d, 0.1);
    }

    [Test]
    public void Offsets_AreSpreadSoTheAgentsFormACanon()
    {
        var context = RotationTestContexts.Scenario4();
        var template = RotationTemplate.Build(context)!;

        var rows = context.Agents
            .Select(a => string.Concat(Enumerable.Range(0, template.Pattern.Count).Select(d => template.Expected(a.Id, context.PeriodFrom.AddDays(d)))))
            .ToList();

        rows.Distinct().Count().ShouldBeGreaterThan(rows.Count / 2);
        template.Expected(context.Agents[0].Id, context.PeriodFrom.AddDays(template.Pattern.Count))
            .ShouldBe(template.Expected(context.Agents[0].Id, context.PeriodFrom));
    }

    [Test]
    public void AgentWithoutShiftWork_IsNotPartOfTheCanon()
    {
        var context = RotationTestContexts.Scenario4();
        var dayWorker = context.Agents[0] with { Id = "DAY", PerformsShiftWork = false };
        var withDayWorker = new CoreWizardContext
        {
            PeriodFrom = context.PeriodFrom,
            PeriodUntil = context.PeriodUntil,
            Agents = context.Agents.Append(dayWorker).ToList(),
            Shifts = context.Shifts,
            SchedulingMaxConsecutiveDays = 6,
        };

        RotationTemplate.Build(withDayWorker)!.Expected("DAY", context.PeriodFrom).ShouldBeNull();
    }

    [Test]
    public void NoShiftDemand_GivesNoTemplate()
    {
        var context = RotationTestContexts.Scenario4();
        var empty = new CoreWizardContext { PeriodFrom = context.PeriodFrom, PeriodUntil = context.PeriodUntil, Agents = context.Agents };

        RotationTemplate.Build(empty).ShouldBeNull();
    }

    [Test]
    public void CarryIn_OffsetContinuesTheOpenNightBlock()
    {
        // Arrange: MA01 enters the period after two nights of a night package.
        var context = WithHistory(RotationTestContexts.Scenario4(), "MA01", shiftKind: 2, days: 2);

        // Act
        var template = RotationTemplate.Build(context)!;

        // Assert: the canon keeps MA01 on nights until the night block of the pattern ends.
        var nightBlock = Blocks(template.Pattern).Single(b => b.Kind == 2).Length;
        for (var d = 0; d < nightBlock - 2; d++)
        {
            template.Expected("MA01", context.PeriodFrom.AddDays(d)).ShouldBe(2);
        }

        template.Expected("MA01", context.PeriodFrom.AddDays(nightBlock - 2)).ShouldBe(RotationTemplate.FreeDay);
    }

    [Test]
    public void CarryIn_FinishedBlockStartsThePeriodWithRest()
    {
        // Arrange: MA02 finished a full early package on the last day of the previous period.
        var context = WithHistory(RotationTestContexts.Scenario4(), "MA02", shiftKind: 0, days: 5);

        // Act
        var template = RotationTemplate.Build(context)!;

        // Assert
        template.Expected("MA02", context.PeriodFrom).ShouldBe(RotationTemplate.FreeDay);
        var firstWork = Enumerable.Range(0, template.Pattern.Count)
            .Select(d => template.Expected("MA02", context.PeriodFrom.AddDays(d)))
            .First(e => e != RotationTemplate.FreeDay);
        firstWork.ShouldBe(1);
    }

    private static CoreWizardContext WithHistory(CoreWizardContext context, string agentId, int shiftKind, int days)
    {
        var start = shiftKind switch { 0 => new TimeOnly(7, 0), 1 => new TimeOnly(15, 0), _ => new TimeOnly(23, 0) };
        var works = Enumerable.Range(1, days)
            .Select(back =>
            {
                var date = context.PeriodFrom.AddDays(-back);
                var startAt = date.ToDateTime(start);
                return new CoreLockedWork($"W{back}", agentId, date, shiftKind, 8m, startAt, startAt.AddHours(8), Guid.NewGuid(), "O0");
            })
            .ToList();

        return new CoreWizardContext
        {
            PeriodFrom = context.PeriodFrom,
            PeriodUntil = context.PeriodUntil,
            Agents = context.Agents,
            Shifts = context.Shifts,
            BreakBlockers = context.BreakBlockers,
            BoundaryLockedWorks = works,
            SchedulingMaxConsecutiveDays = context.SchedulingMaxConsecutiveDays,
            SchedulingMinPauseHours = context.SchedulingMinPauseHours,
            SchedulingMaxDailyHours = context.SchedulingMaxDailyHours,
            SchedulingMaxWeeklyHours = context.SchedulingMaxWeeklyHours,
        };
    }
}
