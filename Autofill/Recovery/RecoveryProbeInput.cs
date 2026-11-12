// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: translates a finished autofill plan and its context into the pieces of a
/// <see cref="RecoverySnapshot"/> once, so every engine run of the probe starts from the same unchanged plan. Every
/// assignment of the plan becomes a work of its holder (locked tokens stay locked); locked works not already in the plan,
/// the existing works and the boundary works become locked occupancy. Caps the autofill leaves at 0 are resolved to the
/// context default exactly like the autofill resolves them, because the engine reads 0 as "unconstrained". An open slot
/// is handed to the engine as the work of a phantom holder that is not in the candidate pool.
/// </summary>
/// <param name="agents">Candidate pool: every non-stand-in agent of the context</param>
/// <param name="works">Occupancy per (agent, day) without the phantom</param>
/// <param name="availability">Day gates per (agent, day)</param>
/// <param name="ineligible">Qualification, keyword and restricted-window gates</param>
/// <param name="days">Planning period</param>
/// <param name="firstMovableDay">First day a chain may take a work from its holder</param>
/// <param name="namesByGuid">Agent id of the context per engine id</param>
public sealed class RecoveryProbeInput(
    IReadOnlyList<RecoveryAgent> agents,
    IReadOnlyDictionary<CellKey, List<RecoveryWork>> works,
    IReadOnlyDictionary<CellKey, DayAvailability> availability,
    IReadOnlySet<IneligibleKey> ineligible,
    IReadOnlyList<DateOnly> days,
    DateOnly firstMovableDay,
    IReadOnlyDictionary<Guid, string> namesByGuid)
{
    public static readonly Guid PhantomId = new("0f0f0f0f-0f0f-0f0f-0f0f-0f0f0f0f0f0f");

    private static readonly Guid ReceivingGroupId = new("0e0e0e0e-0e0e-0e0e-0e0e-0e0e0e0e0e0e");

    private const int EarlyIndex = 0;

    private const int LateIndex = 1;

    private const int NightIndex = 2;

    private const int GuidTailBytes = 8;

    /// <summary>Rule line per agent: the value handed to the engine and where it came from.</summary>
    public List<string> RuleLines { get; } = [];

    /// <summary>Data-dependent assumption lines (counts of what the engine cannot see).</summary>
    public List<string> AssumptionLines { get; } = [];

    /// <summary>Candidate pool size.</summary>
    public int CandidateCount => agents.Count;

    /// <summary>Context agent id of an engine id; the phantom is named as the open slot.</summary>
    /// <param name="agentId">Engine id</param>
    public string NameOf(Guid agentId)
        => agentId == PhantomId
            ? RecoveryProbeFormats.OpenSlotHolder
            : namesByGuid.TryGetValue(agentId, out var name) ? name : agentId.ToString();

    /// <summary>A snapshot of the unchanged plan in which the phantom holds exactly the given open slots.</summary>
    /// <param name="openSlots">Slots the phantom holds; one demand each</param>
    public RecoverySnapshot Snapshot(IReadOnlyList<RecoveryProbeOpenSlot> openSlots)
    {
        var all = new Dictionary<CellKey, IReadOnlyList<RecoveryWork>>(works.Count + openSlots.Count);
        foreach (var (key, list) in works)
        {
            all[key] = list;
        }

        foreach (var group in openSlots.GroupBy(s => s.Date))
        {
            all[new CellKey(PhantomId, group.Key)] = group.Select(PhantomWork).ToList();
        }

        return new RecoverySnapshot(
            days,
            agents,
            all,
            availability,
            ineligible,
            nonCriticalSlots: null,
            receivingGroupId: ReceivingGroupId,
            blockingAbsences: null,
            firstMovableDay: firstMovableDay);
    }

    /// <summary>Builds the snapshot pieces of a finished plan.</summary>
    /// <param name="plan">Finished autofill plan</param>
    /// <param name="context">Context the plan was produced from</param>
    public static RecoveryProbeInput Build(CoreScenario plan, CoreWizardContext context)
    {
        var guidById = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var namesByGuid = new Dictionary<Guid, string>();
        for (var index = 0; index < context.Agents.Count; index++)
        {
            var agentGuid = new Guid(index + 1, 0, 0, new byte[GuidTailBytes]);
            if (guidById.TryAdd(context.Agents[index].Id, agentGuid))
            {
                namesByGuid[agentGuid] = context.Agents[index].Id;
            }
        }

        var works = BuildWorks(plan, context, guidById, out var skippedTokens, out var periodLocked, out var boundaryLocked, out var existing);
        var ruleLines = new List<string>();
        var assumptionLines = new List<string>();
        var result = new RecoveryProbeInput(
            BuildAgents(plan, context, guidById, ruleLines, assumptionLines),
            works,
            BuildAvailability(context, guidById, works, out var breakDays, out var freeDays),
            BuildIneligible(context, guidById, out var keywordKeys, out var windowKeys, out var qualificationKeys),
            Days(context.PeriodFrom, context.PeriodUntil),
            context.PeriodFrom,
            namesByGuid);
        result.RuleLines.AddRange(ruleLines);
        result.AssumptionLines.AddRange(assumptionLines);
        result.AssumptionLines.Add(
            $"plan tokens of agents unknown to the context, left out: {Count(skippedTokens)}; added as locked occupancy: period locked works not in the plan (neither by work id nor as locked token) {Count(periodLocked)}, boundary locked works {Count(boundaryLocked)}, existing and boundary existing works {Count(existing)}");
        result.AssumptionLines.Add(
            $"gates handed to the engine: break days {Count(breakDays)} (whole day, no time window, like the autofill's date veto), FREE days {Count(freeDays)}, qualification gates {Count(qualificationKeys)}, keyword gates (OnlyX/NoX as per-slot ineligibility) {Count(keywordKeys)}, restricted-window gates {Count(windowKeys)}");
        return result;
    }

    private static List<RecoveryAgent> BuildAgents(
        CoreScenario plan,
        CoreWizardContext context,
        IReadOnlyDictionary<string, Guid> guidById,
        List<string> ruleLines,
        List<string> assumptionLines)
    {
        var plannedHours = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var token in plan.Tokens)
        {
            plannedHours[token.AgentId] = plannedHours.GetValueOrDefault(token.AgentId) + token.TotalHours;
        }

        var preferred = PreferenceSets(context, ShiftPreferenceKind.Preferred);
        var blacklisted = PreferenceSets(context, ShiftPreferenceKind.Blacklist);
        var dailyOverrides = context.ContractDays.Count(d => d.MaximumHoursPerDay > 0);
        if (dailyOverrides > 0)
        {
            assumptionLines.Add(
                $"per-date contract daily caps (CoreContractDay.MaximumHoursPerDay) not handed to the engine: {Count(dailyOverrides)} days; the engine uses one daily cap per agent");
        }

        var agents = new List<RecoveryAgent>();
        foreach (var agent in context.Agents)
        {
            if (!guidById.TryGetValue(agent.Id, out var agentGuid) || agent.IsStandIn)
            {
                ruleLines.Add($"RULE agent={agent.Id} standIn={agent.IsStandIn} excluded from the candidate pool, its works stay occupancy");
                continue;
            }

            var (weekly, weeklySource) = Effective(agent.MaxWeeklyHours, context.SchedulingMaxWeeklyHours);
            var (pause, pauseSource) = Effective(agent.MinRestHours, context.SchedulingMinPauseHours);
            var (consecutive, consecutiveSource) = Effective(agent.MaxConsecutiveDays, context.SchedulingMaxConsecutiveDays);
            var (daily, dailySource) = Effective(agent.MaxDailyHours, context.SchedulingMaxDailyHours);
            var current = (decimal)agent.CurrentHours + plannedHours.GetValueOrDefault(agent.Id);
            var deficit = (decimal)agent.GuaranteedHours - current;

            agents.Add(new RecoveryAgent(
                agentGuid,
                agent.Id,
                weekly,
                (int)consecutive,
                pause,
                deficit,
                preferred.GetValueOrDefault(agent.Id) ?? new HashSet<Guid>(),
                blacklisted.GetValueOrDefault(agent.Id) ?? new HashSet<Guid>(),
                IsInGroup: true,
                MaxDailyHours: daily,
                PerformsShiftWork: agent.PerformsShiftWork,
                MaximumHours: (decimal)agent.MaximumHours,
                CurrentPeriodHours: current));

            ruleLines.Add(
                $"RULE agent={agent.Id} maxWeeklyHours={Format(weekly)}({weeklySource}) minPauseHours={Format(pause)}({pauseSource}) "
                + $"maxConsecutiveDays={Format(consecutive)}({consecutiveSource}) maxDailyHours={Format(daily)}({dailySource}) "
                + $"maximumHours={Format((decimal)agent.MaximumHours)}(agent, 0 = none) currentPeriodHours={Format(current)}(agent CurrentHours + plan tokens) "
                + $"targetHoursDeficit={Format(deficit)}(GuaranteedHours - currentPeriodHours, ranking only) performsShiftWork={agent.PerformsShiftWork}(agent)");
        }

        return agents;
    }

    private static Dictionary<CellKey, List<RecoveryWork>> BuildWorks(
        CoreScenario plan,
        CoreWizardContext context,
        IReadOnlyDictionary<string, Guid> guidById,
        out int skippedTokens,
        out int periodLockedAdded,
        out int boundaryLockedAdded,
        out int existingAdded)
    {
        var works = new Dictionary<CellKey, List<RecoveryWork>>();
        var planWorkIds = new HashSet<string>(StringComparer.Ordinal);
        skippedTokens = 0;
        foreach (var token in plan.Tokens)
        {
            planWorkIds.UnionWith(token.WorkIds);
            if (!guidById.TryGetValue(token.AgentId, out var agentGuid))
            {
                skippedTokens++;
                continue;
            }

            Add(works, agentGuid, token.Date, new RecoveryWork(
                CategoryOf(token.ShiftTypeIndex),
                token.ShiftRefId == Guid.Empty ? null : token.ShiftRefId,
                token.IsLocked,
                token.StartAt,
                token.EndAt,
                token.TotalHours,
                []));
        }

        var lockedTokens = plan.Tokens
            .Where(t => t.IsLocked)
            .Select(t => (t.AgentId, t.ShiftRefId, t.Date, t.StartAt))
            .ToHashSet();
        var periodLocked = context.LockedWorks
            .Where(w => !planWorkIds.Contains(w.WorkId) && !lockedTokens.Contains((w.AgentId, w.ShiftRefId, w.Date, w.StartAt)))
            .ToList();
        periodLockedAdded = 0;
        boundaryLockedAdded = 0;
        foreach (var locked in periodLocked.Concat(context.BoundaryLockedWorks))
        {
            if (guidById.TryGetValue(locked.AgentId, out var agentGuid))
            {
                var inPeriod = locked.Date >= context.PeriodFrom && locked.Date <= context.PeriodUntil;
                periodLockedAdded += inPeriod ? 1 : 0;
                boundaryLockedAdded += inPeriod ? 0 : 1;
                Add(works, agentGuid, locked.Date, new RecoveryWork(
                    CategoryOf(locked.ShiftTypeIndex), locked.ShiftRefId, true, locked.StartAt, locked.EndAt, locked.TotalHours, []));
            }
        }

        existingAdded = 0;
        foreach (var existing in context.ExistingWorkBlockers.Concat(context.BoundaryExistingWorkBlockers))
        {
            if (guidById.TryGetValue(existing.AgentId, out var agentGuid))
            {
                existingAdded++;
                var kind = ShiftTypeInference.FromSpan(TimeOnly.FromDateTime(existing.StartAt), TimeOnly.FromDateTime(existing.EndAt));
                Add(works, agentGuid, existing.Date, new RecoveryWork(
                    CategoryOf(kind), null, true, existing.StartAt, existing.EndAt, existing.Hours, []));
            }
        }

        return works;
    }

    private static Dictionary<CellKey, DayAvailability> BuildAvailability(
        CoreWizardContext context,
        IReadOnlyDictionary<string, Guid> guidById,
        IReadOnlyDictionary<CellKey, List<RecoveryWork>> works,
        out int breakDays,
        out int freeDays)
    {
        var from = works.Keys.Select(k => k.Date).Append(context.PeriodFrom).Min();
        var until = works.Keys.Select(k => k.Date).Append(context.PeriodUntil).Max();
        var breaks = new HashSet<(string, DateOnly)>();
        foreach (var blocker in context.BreakBlockers.Concat(context.BoundaryBreakBlockers))
        {
            for (var day = blocker.FromInclusive; day <= blocker.UntilInclusive; day = day.AddDays(1))
            {
                breaks.Add((blocker.AgentId, day));
            }
        }

        var free = context.ScheduleCommands
            .Where(c => c.Keyword == ScheduleCommandKeyword.Free)
            .Select(c => (c.AgentId, c.Date))
            .ToHashSet();

        var availability = new Dictionary<CellKey, DayAvailability>();
        breakDays = 0;
        freeDays = 0;
        foreach (var agent in context.Agents)
        {
            if (!guidById.TryGetValue(agent.Id, out var agentGuid))
            {
                continue;
            }

            for (var day = from; day <= until; day = day.AddDays(1))
            {
                var hasBreak = breaks.Contains((agent.Id, day));
                var hasFree = free.Contains((agent.Id, day));
                breakDays += hasBreak ? 1 : 0;
                freeDays += hasFree ? 1 : 0;
                availability[new CellKey(agentGuid, day)] = new DayAvailability(
                    context.WorksOnDate(agent.Id, day) ?? agent.WorksOnWeekday(day.DayOfWeek),
                    hasFree,
                    hasBreak);
            }
        }

        return availability;
    }

    private static HashSet<IneligibleKey> BuildIneligible(
        CoreWizardContext context,
        IReadOnlyDictionary<string, Guid> guidById,
        out int keywordKeys,
        out int windowKeys,
        out int qualificationKeys)
    {
        var ineligible = new HashSet<IneligibleKey>();
        qualificationKeys = 0;
        foreach (var (agentId, shiftId, date) in context.IneligibleAssignments)
        {
            if (guidById.TryGetValue(agentId, out var agentGuid) && ineligible.Add(new IneligibleKey(agentGuid, shiftId, date)))
            {
                qualificationKeys++;
            }
        }

        var slots = EvaluationContext.For(context).SlotsByKey
            .Select(e => RecoveryProbeOpenSlotFinder.SlotOf(e.Key.ShiftRefId, e.Key.Date, e.Value))
            .OfType<RecoveryProbeOpenSlot>()
            .ToList();
        var slotsByDate = slots.GroupBy(s => s.Date).ToDictionary(g => g.Key, g => g.ToList());

        keywordKeys = 0;
        foreach (var command in context.ScheduleCommands)
        {
            if (!guidById.TryGetValue(command.AgentId, out var agentGuid) || !slotsByDate.TryGetValue(command.Date, out var daySlots))
            {
                continue;
            }

            foreach (var slot in daySlots.Where(s => Forbids(command.Keyword, s.ShiftTypeIndex)))
            {
                keywordKeys += ineligible.Add(new IneligibleKey(agentGuid, slot.ShiftRefId, slot.Date)) ? 1 : 0;
            }
        }

        windowKeys = 0;
        foreach (var slot in slots.Where(s => context.RestrictedTimeWindows.Any(w => w.Blocks(s.StartAt, s.EndAt, s.ShiftRefId))))
        {
            foreach (var agentGuid in guidById.Values)
            {
                windowKeys += ineligible.Add(new IneligibleKey(agentGuid, slot.ShiftRefId, slot.Date)) ? 1 : 0;
            }
        }

        return ineligible;
    }

    private static bool Forbids(ScheduleCommandKeyword keyword, int shiftTypeIndex) => keyword switch
    {
        ScheduleCommandKeyword.OnlyEarly => shiftTypeIndex != EarlyIndex,
        ScheduleCommandKeyword.NoEarly => shiftTypeIndex == EarlyIndex,
        ScheduleCommandKeyword.OnlyLate => shiftTypeIndex != LateIndex,
        ScheduleCommandKeyword.NoLate => shiftTypeIndex == LateIndex,
        ScheduleCommandKeyword.OnlyNight => shiftTypeIndex != NightIndex,
        ScheduleCommandKeyword.NoNight => shiftTypeIndex == NightIndex,
        _ => false,
    };

    private static Dictionary<string, HashSet<Guid>> PreferenceSets(CoreWizardContext context, ShiftPreferenceKind kind)
    {
        var sets = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var preference in context.ShiftPreferences.Where(p => p.Kind == kind))
        {
            if (!sets.TryGetValue(preference.AgentId, out var set))
            {
                set = [];
                sets[preference.AgentId] = set;
            }

            set.Add(preference.ShiftRefId);
        }

        return sets;
    }

    /// <summary>A fresh work of the phantom holder for one open slot.</summary>
    /// <param name="slot">Open slot</param>
    public static RecoveryWork PhantomWork(RecoveryProbeOpenSlot slot)
        => new(CategoryOf(slot.ShiftTypeIndex), slot.ShiftRefId, false, slot.StartAt, slot.EndAt, slot.Hours, []);

    /// <summary>Engine category of an autofill shift kind.</summary>
    /// <param name="shiftTypeIndex">0 early, 1 late, 2 night</param>
    public static ShiftCategory CategoryOf(int shiftTypeIndex) => shiftTypeIndex switch
    {
        EarlyIndex => ShiftCategory.Early,
        LateIndex => ShiftCategory.Late,
        NightIndex => ShiftCategory.Night,
        _ => ShiftCategory.Other,
    };

    private static (decimal Value, string Source) Effective(double agentValue, double contextValue)
        => agentValue > 0 ? ((decimal)agentValue, "agent") : ((decimal)contextValue, "context default");

    private static (decimal Value, string Source) Effective(int agentValue, int contextValue)
        => agentValue > 0 ? (agentValue, "agent") : (contextValue, "context default");

    private static void Add(Dictionary<CellKey, List<RecoveryWork>> works, Guid agentGuid, DateOnly date, RecoveryWork work)
    {
        var key = new CellKey(agentGuid, date);
        if (!works.TryGetValue(key, out var list))
        {
            list = [];
            works[key] = list;
        }

        list.Add(work);
    }

    private static List<DateOnly> Days(DateOnly from, DateOnly until)
    {
        var days = new List<DateOnly>();
        for (var day = from; day <= until; day = day.AddDays(1))
        {
            days.Add(day);
        }

        return days;
    }

    private static string Format(decimal value) => value.ToString(RecoveryProbeFormats.Hours, CultureInfo.InvariantCulture);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
