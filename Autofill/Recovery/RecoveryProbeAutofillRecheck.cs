// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: replays the moves of a recovery proposal on the autofill plan and asks the
/// autofill's own assignment filter (<see cref="SlotConstraintFilter.IsValidAssignment"/> on rung
/// <see cref="SlotRelaxation.All"/>, which holds every hard rule incl. package rest and weekly rest days and lets only the
/// MaxWorkDays ideal step aside) whether every touched person's assignments are still admitted. Only refusals the plan
/// did not already have count, so a deviation the autofill accepted itself never marks a chain as refused. Each refusal is
/// named by the first public building block that refuses it, or by the single rule whose relaxation on a copy of the
/// employee admits it ("several-or-other" when none does alone).
/// </summary>
/// <param name="plan">Finished autofill plan, never modified</param>
/// <param name="context">Context the plan was produced from</param>
/// <param name="input">Snapshot pieces, for the engine-id to agent-id mapping</param>
public sealed class RecoveryProbeAutofillRecheck(CoreScenario plan, CoreWizardContext context, RecoveryProbeInput input)
{
    private readonly EvaluationContext _evaluation = EvaluationContext.For(context);

    /// <summary>The verdict on the replayed moves: ok, the refused assignments, or why the moves could not be replayed.</summary>
    /// <param name="deltas">Moves of one proposal in engine order</param>
    /// <param name="openSlots">Open slots the phantom held in the run</param>
    public RecoveryProbeRecheckResult Verdict(IReadOnlyList<CellDelta> deltas, IReadOnlyList<RecoveryProbeOpenSlot> openSlots)
    {
        var tokens = plan.Tokens.ToList();
        var touched = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var delta in deltas)
        {
            var to = input.NameOf(delta.ToAgentId);
            touched.Add(to);
            if (delta.FromAgentId == RecoveryProbeInput.PhantomId)
            {
                var slot = openSlots.FirstOrDefault(s => s.ShiftRefId == delta.ShiftId && s.Date == delta.Date);
                if (slot is null)
                {
                    return NotReplayable("no open slot for a cover move");
                }

                tokens.Add(TokenFor(slot, to));
                continue;
            }

            var from = input.NameOf(delta.FromAgentId);
            touched.Add(from);
            var index = tokens.FindIndex(t => !t.IsLocked
                && string.Equals(t.AgentId, from, StringComparison.Ordinal)
                && t.ShiftRefId == delta.ShiftId
                && t.Date == delta.Date
                && t.StartAt == delta.StartAt);
            if (index < 0)
            {
                return NotReplayable($"{from} holds no movable token of the moved work on {delta.Date.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture)}");
            }

            tokens[index] = tokens[index] with { AgentId = to };
        }

        var refusals = new List<string>();
        var refusedAgents = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var agentId in touched)
        {
            if (!_evaluation.AgentsById.TryGetValue(agentId, out var agent))
            {
                continue;
            }

            var before = Refused(agent, plan.Tokens).Select(Key).ToHashSet();
            var fresh = Refused(agent, tokens).Where(t => !before.Contains(Key(t))).ToList();
            if (fresh.Count > 0)
            {
                refusedAgents.Add(agentId);
            }

            refusals.AddRange(fresh.Select(t => $"{agentId} {Label(t)} ({RuleOf(agent, t, tokens.Where(o => !ReferenceEquals(o, t)).ToList())})"));
        }

        return refusals.Count == 0
            ? new RecoveryProbeRecheckResult(true, "ok", refusedAgents)
            : new RecoveryProbeRecheckResult(false, "refused " + string.Join("; ", refusals), refusedAgents);
    }

    private static RecoveryProbeRecheckResult NotReplayable(string reason)
        => new(false, "not replayable: " + reason, new SortedSet<string>(StringComparer.Ordinal));

    private string RuleOf(CoreAgent agent, CoreToken token, List<CoreToken> others)
    {
        if (SlotConstraintFilter.HasHardTemporalCollision(agent.Id, token.StartAt, token.EndAt, context, others))
        {
            return "collision";
        }

        if (WeeklyRestDayGuard.Violates(agent, token.Date, others, context, token.StartAt, token.EndAt))
        {
            return "weekly-rest-days";
        }

        if (SlotConstraintFilter.ViolatesAbsoluteVeto(
                agent, token.Date, token.ShiftTypeIndex, token.ShiftRefId, context, others, token.StartAt, token.EndAt))
        {
            return "day-veto";
        }

        if (WeeklyHoursGuard.Violates(agent, token.Date, token.TotalHours, others, context))
        {
            return "weekly-hours";
        }

        if (ExceedsContractDayCap(agent, token, others))
        {
            return "daily-hours(contract day)";
        }

        (string Rule, CoreAgent Relaxed)[] relaxations =
        [
            ("package-rest(MinRestDays)", agent with { MinRestDays = 0 }),
            ("min-pause", agent with { MinRestHours = double.Epsilon }),
            ("consecutive-days-cap", agent with { MaxConsecutiveDays = int.MaxValue }),
            ("daily-hours", agent with { MaxDailyHours = double.MaxValue }),
            ("maximum-hours", agent with { MaximumHours = 0 }),
        ];
        foreach (var (rule, relaxed) in relaxations)
        {
            if (SlotConstraintFilter.IsValidAssignment(
                    relaxed, token.Date, token.ShiftTypeIndex, token.ShiftRefId, token.TotalHours,
                    context, others, token.StartAt, token.EndAt, SlotRelaxation.All))
            {
                return rule;
            }
        }

        return "several-or-other";
    }

    private bool ExceedsContractDayCap(CoreAgent agent, CoreToken token, IReadOnlyList<CoreToken> others)
    {
        var cap = context.ContractDays
            .Where(d => string.Equals(d.AgentId, agent.Id, StringComparison.Ordinal) && d.Date == token.Date && d.MaximumHoursPerDay > 0)
            .Select(d => d.MaximumHoursPerDay)
            .FirstOrDefault();
        var dayHours = others
            .Where(o => string.Equals(o.AgentId, agent.Id, StringComparison.Ordinal) && o.Date == token.Date)
            .Sum(o => o.TotalHours) + token.TotalHours;
        return cap > 0 && (double)dayHours > cap;
    }

    private List<CoreToken> Refused(CoreAgent agent, IReadOnlyList<CoreToken> tokens)
    {
        var refused = new List<CoreToken>();
        foreach (var token in tokens.Where(t => string.Equals(t.AgentId, agent.Id, StringComparison.Ordinal)))
        {
            var others = tokens.Where(t => !ReferenceEquals(t, token)).ToList();
            if (!SlotConstraintFilter.IsValidAssignment(
                    agent, token.Date, token.ShiftTypeIndex, token.ShiftRefId, token.TotalHours,
                    context, others, token.StartAt, token.EndAt, SlotRelaxation.All))
            {
                refused.Add(token);
            }
        }

        return refused;
    }

    private CoreToken TokenFor(RecoveryProbeOpenSlot slot, string agentId)
        => new(
            WorkIds: [],
            ShiftTypeIndex: slot.ShiftTypeIndex,
            Date: slot.Date,
            TotalHours: slot.Hours,
            StartAt: slot.StartAt,
            EndAt: slot.EndAt,
            BlockId: Guid.Empty,
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: _evaluation.LocationContextByShift.GetValueOrDefault(slot.ShiftRefId),
            ShiftRefId: slot.ShiftRefId,
            AgentId: agentId);

    private static (Guid, DateOnly, DateTime) Key(CoreToken token) => (token.ShiftRefId, token.Date, token.StartAt);

    private static string Label(CoreToken token)
        => token.Date.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture) + " "
            + token.StartAt.ToString(RecoveryProbeFormats.Time, CultureInfo.InvariantCulture) + "-"
            + token.EndAt.ToString(RecoveryProbeFormats.Time, CultureInfo.InvariantCulture) + " [" + token.ShiftRefId + "]";
}
