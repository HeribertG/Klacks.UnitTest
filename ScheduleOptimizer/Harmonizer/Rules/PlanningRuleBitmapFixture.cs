// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Rules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Rules;

/// <summary>
/// Seeded bitmap inputs that exercise every planning-rule edge the bitmap hooks must get right: period starting on a
/// Wednesday (week carry-in through the boundary), boundary works and nights on both sides, month carry-in beyond the
/// boundary, cells merged from two works, locked breaks, agents with and without a night window and with different
/// workloads, night shifts 23:00-07:00 and short early starts (05:30) that must NOT count as night with a 60-minute
/// minimum overlap. The oracle plan is built independently of BitmapRuleProjection: it re-adds the original segments
/// of every cell through RulePlan.TryAdd.
/// </summary>
internal static class PlanningRuleBitmapFixture
{
    public const int AgentCount = 5;
    public const int DayCount = 16;
    public const int BoundaryDays = 6;
    public const int NightMinOverlap = 60;

    public static readonly DateOnly From = new(2026, 3, 4);
    public static readonly CoreNightWindow NightWindow = new(new TimeOnly(23, 0), new TimeOnly(6, 0));

    private static readonly decimal[] Workloads = [100m, 80m, 50m, 100m, 60m];
    private static readonly Guid EarlyShift = new("00000000-0000-0000-0000-00000000e001");
    private static readonly Guid LateShift = new("00000000-0000-0000-0000-00000000a002");
    private static readonly Guid NightShift = new("00000000-0000-0000-0000-00000000c003");
    private static readonly Guid DawnShift = new("00000000-0000-0000-0000-00000000d004");

    public static DateOnly Until => From.AddDays(DayCount - 1);

    public static IReadOnlyList<PlanRule> HardRules() =>
    [
        new MaxConsecutiveOfKindRule(new Guid("10000000-0000-0000-0000-000000000001"), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2),
        new RestAfterKindRule(new Guid("10000000-0000-0000-0000-000000000002"), RuleSeverity.Hard, 1, RuleShiftKind.Night, 2),
        new ForbiddenTransitionRule(new Guid("10000000-0000-0000-0000-000000000003"), RuleSeverity.Hard, 1, RuleShiftKind.Night, RuleShiftKind.Early, 1),
        new PeriodCountRule(new Guid("10000000-0000-0000-0000-000000000004"), RuleSeverity.Hard, 1, RuleCounterEvent.NightShift, RuleCalendarPeriod.Month, 6),
        new PeriodCountRule(new Guid("10000000-0000-0000-0000-000000000005"), RuleSeverity.Hard, 1, RuleCounterEvent.WorkedDayInWeek, RuleCalendarPeriod.Week, 6,
            AgentScope: new HashSet<string>(StringComparer.Ordinal) { AgentId(1), AgentId(3) }),
    ];

    public static IReadOnlyList<PlanRule> SoftRules() =>
    [
        new MaxConsecutiveOfKindRule(new Guid("20000000-0000-0000-0000-000000000001"), RuleSeverity.Soft, 2, RuleShiftKind.Work, 4),
        new ForbiddenTransitionRule(new Guid("20000000-0000-0000-0000-000000000002"), RuleSeverity.Soft, 0.5, RuleShiftKind.Late, RuleShiftKind.Early, 1),
        new TeamFairnessRule(new Guid("20000000-0000-0000-0000-000000000003"), 1.5, FairnessMetric.NightDays, FairnessWindow.PlanPeriod, 1m, true,
            new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }),
        new TeamFairnessRule(new Guid("20000000-0000-0000-0000-000000000004"), 1, FairnessMetric.WeekendDays, FairnessWindow.Week, 0m, false,
            new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }),
    ];

    public static IReadOnlyList<PlanRule> MixedRules() => [.. HardRules(), .. SoftRules()];

    public static string AgentId(int index) => $"agent-{index}";

    /// <summary>Random input with the given rules; null rules = an input without BitmapInput.Rules.</summary>
    public static BitmapInput Build(int seed, IReadOnlyList<PlanRule>? rules, IReadOnlySet<Guid>? ignoredWorkIds = null)
    {
        var random = new Random(seed);
        var agents = Enumerable.Range(0, AgentCount)
            .Select(i => new BitmapAgent(
                Id: AgentId(i),
                DisplayName: AgentId(i),
                TargetHours: 80m,
                PreferredShiftSymbols: new HashSet<CellSymbol>(),
                NightWindow: i == 0 ? null : NightWindow,
                WorkloadPercent: Workloads[i]))
            .ToList();

        var assignments = new List<BitmapAssignment>();
        var boundary = new List<BitmapAssignment>();
        for (var agent = 0; agent < AgentCount; agent++)
        {
            for (var offset = -BoundaryDays; offset < DayCount + BoundaryDays; offset++)
            {
                var date = From.AddDays(offset);
                var inPeriod = offset >= 0 && offset < DayCount;
                var target = inPeriod ? assignments : boundary;
                if (inPeriod && random.NextDouble() < 0.05)
                {
                    target.Add(Break(AgentId(agent), date, random));
                    continue;
                }

                AddDay(target, AgentId(agent), date, random);
            }
        }

        var carryIn = new List<RuleSegment>();
        for (var agent = 1; agent < AgentCount; agent++)
        {
            var date = Until.AddDays(BoundaryDays + 1 + agent);
            carryIn.Add(new RuleSegment(AgentId(agent), date, new TimeOnly(23, 0), new TimeOnly(7, 0), 2, 8m));
        }

        return new BitmapInput(
            agents,
            From,
            Until,
            assignments,
            BoundaryAssignments: boundary,
            Rules: rules is null ? null : new BitmapPlanningRules(rules, carryIn, NightMinOverlap, ignoredWorkIds));
    }

    /// <summary>Rule context exactly as the runtime builds it (the established FromBitmap adapter).</summary>
    public static RuleEvaluationContext Context(BitmapInput input)
        => RuleEvaluationContextFactory.FromBitmap(input, input.Rules!.NightRuleMinOverlapMinutes, input.Rules.CarryIn);

    /// <summary>
    /// Independent oracle: every cell's original assignments (looked up by WorkId) re-added through RulePlan.TryAdd,
    /// anchored to the cell's current row agent and day.
    /// </summary>
    public static RulePlan OraclePlan(RuleEvaluationContext context, BitmapInput input, HarmonyBitmap bitmap)
    {
        var ignored = input.Rules?.IgnoredWorkIds ?? new HashSet<Guid>();
        var byWorkId = new Dictionary<Guid, BitmapAssignment>();
        foreach (var assignment in input.Assignments)
        {
            foreach (var workId in assignment.WorkIds)
            {
                byWorkId[workId] = assignment;
            }
        }

        var plan = new RulePlan(context);
        for (var row = 0; row < bitmap.RowCount; row++)
        {
            for (var day = 0; day < bitmap.DayCount; day++)
            {
                var seen = new HashSet<BitmapAssignment>(ReferenceEqualityComparer.Instance);
                foreach (var workId in bitmap.GetCell(row, day).WorkIds)
                {
                    if (ignored.Contains(workId) || !byWorkId.TryGetValue(workId, out var assignment) || !seen.Add(assignment)
                        || assignment.Symbol is CellSymbol.Free or CellSymbol.Break)
                    {
                        continue;
                    }

                    var segment = RuleSegmentMapper.FromBitmapAssignment(assignment) with
                    {
                        AgentId = bitmap.Rows[row].Id,
                        Date = bitmap.Days[day],
                    };
                    plan.TryAdd(segment).ShouldBeTrue();
                }
            }
        }

        return plan;
    }

    /// <summary>Summed hard Excess per (rule, agent) - the unit of the pre-commit delta.</summary>
    public static Dictionary<(Guid RuleId, string? AgentId), decimal> HardExcess(RuleEvaluation evaluation)
        => evaluation.Findings
            .Where(f => f.Severity == RuleSeverity.Hard)
            .GroupBy(f => (f.RuleId, f.AgentId))
            .ToDictionary(g => g.Key, g => g.Sum(f => f.Excess));

    public static void Swap(HarmonyBitmap bitmap, int rowA, int dayA, int rowB, int dayB)
    {
        var cellA = bitmap.GetCell(rowA, dayA);
        bitmap.SetCell(rowA, dayA, bitmap.GetCell(rowB, dayB));
        bitmap.SetCell(rowB, dayB, cellA);
    }

    private static void AddDay(List<BitmapAssignment> target, string agentId, DateOnly date, Random random)
    {
        var roll = random.NextDouble();
        if (roll < 0.3)
        {
            return;
        }

        if (roll < 0.5)
        {
            target.Add(Work(agentId, date, CellSymbol.Early, EarlyShift, new TimeOnly(7, 0), 8, random));
            if (random.NextDouble() < 0.3)
            {
                target.Add(Work(agentId, date, CellSymbol.Late, LateShift, new TimeOnly(16, 0), 2, random));
            }

            return;
        }

        if (roll < 0.62)
        {
            target.Add(Work(agentId, date, CellSymbol.Early, DawnShift, new TimeOnly(5, 30), 8, random));
            return;
        }

        if (roll < 0.78)
        {
            target.Add(Work(agentId, date, CellSymbol.Late, LateShift, new TimeOnly(15, 0), 8, random));
            return;
        }

        target.Add(Work(agentId, date, CellSymbol.Night, NightShift, new TimeOnly(23, 0), 8, random));
    }

    private static BitmapAssignment Work(string agentId, DateOnly date, CellSymbol symbol, Guid shift, TimeOnly start, int hours, Random random)
    {
        var startAt = date.ToDateTime(start, DateTimeKind.Utc);
        return new BitmapAssignment(agentId, date, symbol, shift, [NewGuid(random)], false, startAt, startAt.AddHours(hours), hours);
    }

    private static BitmapAssignment Break(string agentId, DateOnly date, Random random)
        => new(agentId, date, CellSymbol.Break, Guid.Empty, [NewGuid(random)], true, default, default, 8m);

    private static Guid NewGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }
}
