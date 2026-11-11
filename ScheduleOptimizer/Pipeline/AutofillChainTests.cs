// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Pipeline;

/// <summary>
/// The autofill chain Wizard 1 -> Wizard 2 -> Wizard 3 on <see cref="ChainStage"/>, run once per fixture (and a second
/// time to prove it is reproducible). After every stage the plan is measured with this file's own code, not with an engine
/// checker: paid hours per person and ISO week within the weekly cap, rest between two consecutive works at least the
/// minimum pause, the longest run of working days within the maximum, paid hours per person and day within the daily cap,
/// coverage of (duty, day) never below Wizard 1's, and the planner's row order kept by Wizards 2 and 3. Every expectation
/// comes from the rules of the stage, not from an observed run. The plan matrix of each stage (row = person in planner
/// order, column = day, F early / S late / . free) is written to the test output.
/// Not covered: Wizard 3's cross-day swaps (the deterministic stage 3 proposes same-day swaps only; the cross-day end-state
/// check is covered by PlanMutationValidatorCrossDayBypassTests and PlanMutationValidatorDailyHoursTests), breaks, locked
/// works, boundary days, keyword wishes, planning rules, stand-ins, the LLM mode of Wizard 3 and the database apply paths.
/// </summary>
[TestFixture]
public class AutofillChainTests
{
    private const string Wizard1 = "W1";
    private const string Wizard2 = "W2";
    private const string Wizard3 = "W3";
    private const string RowSeparator = ",";

    private ChainStage.ChainRun _run = null!;
    private ChainStage.ChainRun _repeat = null!;
    private IReadOnlyList<(string Stage, IReadOnlyList<ChainStage.PlanEntry> Plan)> _stages = null!;

    [OneTimeSetUp]
    public void RunTheChain()
    {
        _run = ChainStage.RunChain();
        _repeat = ChainStage.RunChain();
        _stages = StagesOf(_run);

        foreach (var (stage, plan) in _stages)
        {
            TestContext.Out.WriteLine(Matrix(stage, plan));
        }

        TestContext.Out.WriteLine(
            $"W3 search: stop {_run.Wizard3Search.StopReason}, fitness {_run.Wizard3Search.FitnessBefore:F4} -> {_run.Wizard3Search.FitnessAfter:F4}, applied {_run.Wizard3Search.AppliedBatches.Count}");
    }

    [Test]
    public void Stage_IsTwoFullIsoWeeks_AndThePlannerOrderDiffersFromTheAutomaticOrder()
    {
        ChainStage.From.DayOfWeek.ShouldBe(DayOfWeek.Monday);
        ChainStage.Until.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        ChainStage.Days().Count().ShouldBe(ChainStage.DayCount);
        Joined(_run.AutomaticRowOrder).ShouldNotBe(Joined(PlannerOrderIds()), "the planner order must differ from the automatic one, otherwise the order check proves nothing");
    }

    [Test]
    public void Chain_IsReproducible()
    {
        _run.Wizard3Search.StopReason.ShouldNotBe(DeterministicSearchStopReason.WallClockBudget, "a stage 3 run stopped by the clock is not reproducible");
        (_run.Wizard3Search.RegionRebuild?.WallClockBudgetHit ?? false).ShouldBeFalse("the region rebuild must not be stopped by the clock");

        var repeated = StagesOf(_repeat);
        for (var i = 0; i < _stages.Count; i++)
        {
            Fingerprint(repeated[i].Plan).ShouldBe(Fingerprint(_stages[i].Plan), $"stage {_stages[i].Stage} differs between two runs of the same chain");
        }
    }

    [Test]
    public void EveryStage_KeepsTheWeeklyCap()
    {
        var violations = new List<string>();
        foreach (var (stage, plan) in _stages)
        {
            foreach (var week in plan.GroupBy(e => (e.AgentId, Year: ISOWeek.GetYear(ToDateTime(e.Date)), Week: ISOWeek.GetWeekOfYear(ToDateTime(e.Date)))))
            {
                var hours = week.Sum(e => e.Hours);
                var cap = (decimal)ChainStage.PersonOf(week.Key.AgentId).MaxWeeklyHours;
                if (hours > cap)
                {
                    violations.Add($"{stage}: {week.Key.AgentId} ISO week {week.Key.Year}-{week.Key.Week}: {hours} h, cap {cap} h");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryStage_KeepsTheMinimumPause()
    {
        var violations = new List<string>();
        foreach (var (stage, plan) in _stages)
        {
            foreach (var person in plan.GroupBy(e => e.AgentId))
            {
                var works = person.OrderBy(e => e.StartAt).ToList();
                for (var i = 1; i < works.Count; i++)
                {
                    var pause = (works[i].StartAt - works[i - 1].EndAt).TotalHours;
                    if (pause < ChainStage.MinRestHours)
                    {
                        violations.Add($"{stage}: {person.Key} {works[i - 1].EndAt:yyyy-MM-dd HH:mm} -> {works[i].StartAt:yyyy-MM-dd HH:mm}: pause {pause} h, minimum {ChainStage.MinRestHours} h");
                    }
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryStage_KeepsTheMaximumConsecutiveDays()
    {
        var violations = new List<string>();
        foreach (var (stage, plan) in _stages)
        {
            foreach (var person in plan.GroupBy(e => e.AgentId))
            {
                var workDays = person.Select(e => e.Date).ToHashSet();
                var run = 0;
                foreach (var day in ChainStage.Days())
                {
                    run = workDays.Contains(day) ? run + 1 : 0;
                    if (run > ChainStage.MaxConsecutiveDays)
                    {
                        violations.Add($"{stage}: {person.Key} works {run} days in a row up to {day:yyyy-MM-dd}, maximum {ChainStage.MaxConsecutiveDays}");
                    }
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void EveryStage_KeepsTheDailyCap()
    {
        var violations = new List<string>();
        foreach (var (stage, plan) in _stages)
        {
            foreach (var day in plan.GroupBy(e => (e.AgentId, e.Date)))
            {
                var hours = day.Sum(e => e.Hours);
                var cap = (decimal)ChainStage.PersonOf(day.Key.AgentId).MaxDailyHours;
                if (hours > cap)
                {
                    violations.Add($"{stage}: {day.Key.AgentId} {day.Key.Date:yyyy-MM-dd}: {hours} h, cap {cap} h");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void LaterStages_NeverLoseCoverage()
    {
        var afterWizard1 = Coverage(_stages[0].Plan);

        Coverage(_stages[1].Plan).ShouldBeGreaterThanOrEqualTo(afterWizard1, $"{Wizard2} covers fewer (duty, day) places than {Wizard1}");
        Coverage(_stages[2].Plan).ShouldBeGreaterThanOrEqualTo(afterWizard1, $"{Wizard3} covers fewer (duty, day) places than {Wizard1}");
    }

    [Test]
    public void LaterStages_KeepThePlannerRowOrder()
    {
        Joined(_run.Wizard2.Rows.Select(r => r.Id)).ShouldBe(Joined(PlannerOrderIds()), $"{Wizard2} changed the planner's row order");
        Joined(_run.Wizard3.Rows.Select(r => r.Id)).ShouldBe(Joined(PlannerOrderIds()), $"{Wizard3} changed the planner's row order");
    }

    private static IReadOnlyList<(string Stage, IReadOnlyList<ChainStage.PlanEntry> Plan)> StagesOf(ChainStage.ChainRun run) =>
    [
        (Wizard1, run.Wizard1),
        (Wizard2, ChainStage.EntriesOf(run.Wizard2)),
        (Wizard3, ChainStage.EntriesOf(run.Wizard3)),
    ];

    private static IReadOnlyList<string> PlannerOrderIds() => ChainStage.PlannerOrder.Select(p => p.Id).ToList();

    private static string Joined(IEnumerable<string> ids) => string.Join(RowSeparator, ids);

    private static DateTime ToDateTime(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    private static int Coverage(IReadOnlyList<ChainStage.PlanEntry> plan)
        => plan.Select(e => (e.ShiftRefId, e.Date)).Distinct().Count();

    private static string Fingerprint(IReadOnlyList<ChainStage.PlanEntry> plan)
        => string.Join(
            Environment.NewLine,
            plan.OrderBy(e => e.AgentId, StringComparer.Ordinal).ThenBy(e => e.StartAt)
                .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.AgentId} {e.Date:yyyy-MM-dd} {e.ShiftRefId} {e.StartAt:HH:mm} {e.EndAt:HH:mm} {e.Hours}")));

    private static string Matrix(string stage, IReadOnlyList<ChainStage.PlanEntry> plan)
    {
        var text = new StringBuilder();
        text.AppendLine($"{stage} (coverage {Coverage(plan)} of {ChainStage.Places})");
        text.Append("   ");
        foreach (var day in ChainStage.Days())
        {
            text.Append(day.ToString("ddd", CultureInfo.InvariantCulture)[0]);
        }

        text.AppendLine();
        foreach (var person in ChainStage.PlannerOrder)
        {
            text.Append(person.Id.PadRight(3));
            var hours = 0m;
            foreach (var day in ChainStage.Days())
            {
                var works = plan.Where(e => e.AgentId == person.Id && e.Date == day).ToList();
                hours += works.Sum(e => e.Hours);
                text.Append(Mark(works));
            }

            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  {hours} h of {person.GuaranteedHours} h"));
        }

        return text.ToString();
    }

    private static char Mark(IReadOnlyList<ChainStage.PlanEntry> works) => works.Count switch
    {
        0 => ChainStage.FreeMark,
        1 => works[0].ShiftRefId == ChainStage.EarlyShiftId ? ChainStage.EarlyMark : ChainStage.LateMark,
        _ => ChainStage.SeveralMark,
    };
}
