// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.ScheduleOptimizer.Pipeline;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// Builds the Wizard 2/3 input from a scenario context the way HarmonizerContextBuilder materialises it from the
/// database: agents in list order with KeepAgentOrder (the planner order of an AgentOrderIsUserDefined run), target
/// hours = guaranteed hours (the bitmap spans exactly the period), weekly, daily, consecutive-day, pause and weekly
/// rest-day caps from the same agent values Wizard 1 reads (production feeds both sides from one contract), the weekday
/// flags as day availability, the previous-month works as boundary assignments, and the eligibility ban list as
/// ineligible (agent, shift, date) triples, the schedule commands as FREE days, required or forbidden shift symbols and
/// keyword days (<see cref="ChainKeywordDays"/>) and the shift preferences as preferred symbols and blacklisted shifts
/// (<see cref="ChainShiftPreferences"/>). A scenario input the factory cannot carry over fails loudly instead of being
/// dropped, so a measurement never runs on a silently weaker rule set.
/// </summary>
public static class ChainBitmapInputFactory
{
    private const short PeriodWorkTag = 1;
    private const short BoundaryWorkTag = 2;
    private const short NoVariant = 0;
    private const int GuidTailLength = 8;
    private const int FirstWorkNumber = 1;

    /// <summary>
    /// The Wizard 1 plan as works with stable ids, ordered by day, shift and agent. Fails when the plan holds a token the
    /// bitmap cannot carry one to one: outside the period, locked, or a second token of one agent on one day (a bitmap
    /// cell merges those).
    /// </summary>
    /// <param name="wizard1">Plan produced by Wizard 1</param>
    /// <param name="definition">Scenario that produced it</param>
    public static IReadOnlyList<ChainWork> WorksOf(CoreScenario wizard1, AutofillScenarioDefinition definition)
    {
        var problems = new List<string>();
        problems.AddRange(wizard1.Tokens
            .Where(t => t.Date < definition.PeriodFrom || t.Date > definition.PeriodUntil)
            .Select(t => $"token outside the period: {t.AgentId} {t.Date:yyyy-MM-dd}"));
        problems.AddRange(wizard1.Tokens
            .Where(t => t.IsLocked)
            .Select(t => $"locked token: {t.AgentId} {t.Date:yyyy-MM-dd}"));
        problems.AddRange(wizard1.Tokens
            .GroupBy(t => (t.AgentId, t.Date))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Count()} tokens of {g.Key.AgentId} on {g.Key.Date:yyyy-MM-dd} would merge into one bitmap cell"));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The Wizard 1 plan cannot be carried into the bitmap one to one: " + string.Join("; ", problems));
        }

        return wizard1.Tokens
            .OrderBy(t => t.Date)
            .ThenBy(t => t.ShiftRefId)
            .ThenBy(t => t.AgentId, StringComparer.Ordinal)
            .Select((token, index) => new ChainWork(WorkIdOf(index, PeriodWorkTag), token))
            .ToList();
    }

    /// <summary>The bitmap input of one stage.</summary>
    /// <param name="definition">Scenario whose hard data the input carries</param>
    /// <param name="works">Works of the plan the stage starts from</param>
    /// <param name="hints">Softening hints of the Wizard 1 run; only the Wizard 2 conductor reads them</param>
    public static BitmapInput Build(
        AutofillScenarioDefinition definition,
        IReadOnlyList<ChainWork> works,
        IReadOnlyList<SofteningHint>? hints)
    {
        EnsureMappable(definition);
        var context = definition.Context;
        return new BitmapInput(
            AgentsOf(context),
            context.PeriodFrom,
            context.PeriodUntil,
            works.Select(AssignmentOf).ToList(),
            SofteningHints: hints,
            Availability: AvailabilityOf(context),
            BoundaryAssignments: BoundaryOf(context),
            IneligibleAssignments: context.IneligibleAssignments,
            RestrictedTimeWindows: context.RestrictedTimeWindows,
            KeywordMode: context.KeywordMode,
            KeepAgentOrder: true);
    }

    private static void EnsureMappable(AutofillScenarioDefinition definition)
    {
        var context = definition.Context;
        var unmapped = new List<string>();
        AddWhenPresent(unmapped, nameof(context.ContractDays), context.ContractDays.Count);
        AddWhenPresent(unmapped, nameof(context.BreakBlockers), context.BreakBlockers.Count);
        AddWhenPresent(unmapped, nameof(context.LockedWorks), context.LockedWorks.Count);
        AddWhenPresent(unmapped, nameof(context.ExistingWorkBlockers), context.ExistingWorkBlockers.Count);
        AddWhenPresent(unmapped, nameof(context.BoundaryBreakBlockers), context.BoundaryBreakBlockers.Count);
        AddWhenPresent(unmapped, nameof(context.BoundaryExistingWorkBlockers), context.BoundaryExistingWorkBlockers.Count);
        AddWhenPresent(unmapped, nameof(CoreAgent.MaximumHours), context.Agents.Count(a => a.MaximumHours > 0));
        if (unmapped.Count > 0)
        {
            throw new InvalidOperationException(
                "The scenario carries hard data the chain measurement does not map into the bitmap input, so Wizard 2/3 "
                + "would run on a weaker rule set than in production: " + string.Join(", ", unmapped));
        }
    }

    private static void AddWhenPresent(List<string> unmapped, string field, int count)
    {
        if (count > 0)
        {
            unmapped.Add($"{field} ({count})");
        }
    }

    private static List<BitmapAgent> AgentsOf(CoreWizardContext context)
    {
        var preferred = ChainShiftPreferences.PreferredSymbols(context);
        var blacklisted = ChainShiftPreferences.BlacklistedShifts(context);
        return context.Agents
            .Select(agent => new BitmapAgent(
                Id: agent.Id,
                DisplayName: agent.Id,
                TargetHours: (decimal)agent.GuaranteedHours,
                PreferredShiftSymbols: preferred.TryGetValue(agent.Id, out var symbols) ? symbols : new HashSet<CellSymbol>(),
                MaxWeeklyHours: (decimal)agent.MaxWeeklyHours,
                MaxConsecutiveDays: agent.MaxConsecutiveDays,
                MinPauseHours: (decimal)agent.MinRestHours,
                BlacklistedShiftIds: blacklisted.GetValueOrDefault(agent.Id),
                MinRestDays: agent.MinRestDays,
                PerformsShiftWork: agent.PerformsShiftWork,
                MaxDailyHours: (decimal)agent.MaxDailyHours))
            .ToList();
    }

    private static Dictionary<(string AgentId, DateOnly Date), DayAvailability> AvailabilityOf(CoreWizardContext context)
    {
        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>();
        var keywordDays = ChainKeywordDays.Of(context);
        foreach (var agent in context.Agents)
        {
            for (var day = context.PeriodFrom; day <= context.PeriodUntil; day = day.AddDays(1))
            {
                var worksOnDay = agent.WorksOnWeekday(day.DayOfWeek);
                keywordDays.TryGetValue((agent.Id, day), out var keywordDay);
                if (keywordDay is not null && !worksOnDay)
                {
                    keywordDay = keywordDay.WithoutPositiveWish();
                }

                var (isFree, required, forbidden) = ChainKeywordDays.RestrictionOf(keywordDay);
                availability[(agent.Id, day)] = new DayAvailability(
                    WorksOnDay: worksOnDay,
                    HasFreeCommand: isFree,
                    HasBreakBlocker: false,
                    RequiredSymbol: required,
                    ForbiddenSymbol: forbidden,
                    Keywords: keywordDay,
                    MaxDailyHours: (decimal)agent.MaxDailyHours);
            }
        }

        return availability;
    }

    private static List<BitmapAssignment> BoundaryOf(CoreWizardContext context)
        => context.BoundaryLockedWorks
            .Select((work, index) => new BitmapAssignment(
                work.AgentId,
                work.Date,
                ChainStage.SymbolOf(work.StartAt, work.EndAt),
                work.ShiftRefId,
                [WorkIdOf(index, BoundaryWorkTag)],
                IsLocked: false,
                work.StartAt,
                work.EndAt,
                work.TotalHours))
            .ToList();

    private static BitmapAssignment AssignmentOf(ChainWork work)
        => new(
            work.Token.AgentId,
            work.Token.Date,
            ChainStage.SymbolOf(work.Token.StartAt, work.Token.EndAt),
            work.Token.ShiftRefId,
            [work.WorkId],
            IsLocked: false,
            work.Token.StartAt,
            work.Token.EndAt,
            work.Token.TotalHours);

    private static Guid WorkIdOf(int index, short tag)
        => new(index + FirstWorkNumber, tag, NoVariant, new byte[GuidTailLength]);
}
