// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// MEASUREMENT, not production code: the one place where the swap-chain probe decides whether an employee may hold a
/// shift. It asks the engine's assignment filter (<see cref="SlotConstraintFilter"/>) and adds two things on top:
/// <para>
/// A variant may shorten the package rest. The filter cannot switch that rule off by itself, so the probe asks it with
/// a copy of the employee whose MinRestDays is zero and checks the weekly rest days, which read the same field, with
/// the original employee. The minimum pause, the hour caps and the MaxConsecutiveDays cap keep applying.
/// </para>
/// <para>
/// An assignment the baseline plan (the engine's own plan) already held in breach of a SOFT rule — package rest or
/// block ideal, the two rules the engine's stage 0 does not veto — is tolerated, so a deviation the engine accepted
/// never blocks a move that recreates it. A breach of any other rule is never tolerated. Whether a tolerated
/// deviation got worse is not measured.
/// </para>
/// <para>
/// A refusal is named through public building blocks only: the collision, weekly-rest, day-veto and weekly-hours
/// checks directly, a shift that ends where the candidate starts (or starts where it ends) as the minimum pause, because
/// the filter refuses a gap of zero hours on every positive pause, and the remaining rules by switching one of them off
/// on a copy of the employee.
/// </para>
/// </summary>
public sealed class SwapChainProbeAdmission
{
    private readonly CoreWizardContext _context;

    private readonly EvaluationContext _evaluation;

    private readonly bool _packageRestMayShorten;

    private readonly HashSet<(string AgentId, Guid ShiftRefId, DateOnly Date)> _tolerated;

    /// <summary>Builds the admission for one measurement.</summary>
    /// <param name="context">Context the plan was produced from</param>
    /// <param name="packageRestMayShorten">True to let the package rest step aside; the minimum pause holds</param>
    /// <param name="baseline">Plan whose soft deviations are tolerated when a move recreates them; null tolerates nothing</param>
    public SwapChainProbeAdmission(CoreWizardContext context, bool packageRestMayShorten, CoreScenario? baseline)
    {
        _context = context;
        _evaluation = EvaluationContext.For(context);
        _packageRestMayShorten = packageRestMayShorten;
        _tolerated = baseline is null
            ? new HashSet<(string AgentId, Guid ShiftRefId, DateOnly Date)>()
            : SoftDeviationsOf(baseline);
    }

    /// <summary>Filter calls spent on admissions so far; the probe budget is counted in these.</summary>
    public long Checks { get; private set; }

    /// <summary>Assignments of the baseline plan that break a soft rule and are therefore tolerated.</summary>
    public int ToleratedCount => _tolerated.Count;

    /// <summary>Looks an employee up by id.</summary>
    /// <param name="agentId">Employee id</param>
    public CoreAgent? AgentOf(string agentId) => _evaluation.AgentsById.GetValueOrDefault(agentId);

    /// <summary>True when the employee may hold the shift next to the other tokens.</summary>
    /// <param name="agent">Employee</param>
    /// <param name="item">Shift</param>
    /// <param name="others">Every other token of the plan</param>
    /// <param name="rung">Rung of the coverage escalation</param>
    public bool Admits(CoreAgent agent, SwapChainProbeItem item, IReadOnlyList<CoreToken> others, SlotRelaxation rung)
    {
        Checks++;
        if (Passes(agent, item, others, rung, _packageRestMayShorten))
        {
            return true;
        }

        return _tolerated.Contains((agent.Id, item.ShiftRefId, item.Date))
            && PassesWithoutSoftRules(agent, item, others, rung);
    }

    /// <summary>The rule that refuses the shift, or null when it is admitted.</summary>
    /// <param name="agent">Employee</param>
    /// <param name="item">Shift</param>
    /// <param name="others">Every other token of the plan</param>
    /// <param name="rung">Rung of the coverage escalation</param>
    public SwapChainProbeRule? Reject(CoreAgent agent, SwapChainProbeItem item, IReadOnlyList<CoreToken> others, SlotRelaxation rung)
        => Admits(agent, item, others, rung) ? null : Classify(agent, item, others, rung, _packageRestMayShorten);

    /// <summary>The employee's tokens that are not admitted next to the rest of the plan.</summary>
    /// <param name="agent">Employee</param>
    /// <param name="tokens">Whole plan</param>
    /// <param name="rung">Rung of the coverage escalation</param>
    public HashSet<CoreToken> Inadmissible(CoreAgent agent, IReadOnlyList<CoreToken> tokens, SlotRelaxation rung)
    {
        var failing = new HashSet<CoreToken>(ReferenceEqualityComparer.Instance);
        foreach (var token in tokens)
        {
            if (string.Equals(token.AgentId, agent.Id, StringComparison.Ordinal)
                && !Admits(agent, SwapChainProbeItem.ForHandedOn(token), Without(tokens, token), rung))
            {
                failing.Add(token);
            }
        }

        return failing;
    }

    /// <summary>
    /// The first token of the employee, in plan order, that is not admitted now and was not in
    /// <paramref name="before"/>, with the rule that refuses it; null when the move adds no refusal.
    /// </summary>
    /// <param name="before">Refused tokens of the employee before the move</param>
    /// <param name="agent">Employee</param>
    /// <param name="tokens">Whole plan after the move</param>
    /// <param name="rung">Rung of the coverage escalation</param>
    public (CoreToken Token, SwapChainProbeRule Rule)? FirstNewInadmissible(
        HashSet<CoreToken> before, CoreAgent agent, IReadOnlyList<CoreToken> tokens, SlotRelaxation rung)
    {
        foreach (var token in tokens)
        {
            if (!string.Equals(token.AgentId, agent.Id, StringComparison.Ordinal) || before.Contains(token))
            {
                continue;
            }

            var item = SwapChainProbeItem.ForHandedOn(token);
            var others = Without(tokens, token);
            if (!Admits(agent, item, others, rung))
            {
                return (token, Classify(agent, item, others, rung, _packageRestMayShorten));
            }
        }

        return null;
    }

    /// <summary>The token the employee holds after taking the shift.</summary>
    /// <param name="item">Shift</param>
    /// <param name="agent">Employee</param>
    public CoreToken TokenFor(SwapChainProbeItem item, CoreAgent agent)
    {
        var surcharges = SurchargeEstimator.Estimate(item.Hours, item.ShiftTypeIndex, item.Date, agent);
        if (item.Source is not null)
        {
            return item.Source with { AgentId = agent.Id, Surcharges = surcharges };
        }

        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: item.ShiftTypeIndex,
            Date: item.Date,
            TotalHours: item.Hours,
            StartAt: item.StartAt,
            EndAt: item.EndAt,
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: _evaluation.LocationContextByShift.GetValueOrDefault(item.ShiftRefId),
            ShiftRefId: item.ShiftRefId,
            AgentId: agent.Id)
        {
            Surcharges = surcharges,
        };
    }

    /// <summary>The tokens of the plan without one of them.</summary>
    /// <param name="tokens">Whole plan</param>
    /// <param name="left">Token to leave out</param>
    public static List<CoreToken> Without(IReadOnlyList<CoreToken> tokens, CoreToken left)
        => tokens.Where(token => !ReferenceEquals(token, left)).ToList();

    private bool Passes(
        CoreAgent agent, SwapChainProbeItem item, IReadOnlyList<CoreToken> others, SlotRelaxation rung, bool packageRestMayShorten)
    {
        if (!packageRestMayShorten)
        {
            return Valid(agent, item, others, rung);
        }

        return Valid(agent with { MinRestDays = 0 }, item, others, rung)
            && !WeeklyRestDayGuard.Violates(agent, item.Date, others, _context, item.StartAt, item.EndAt);
    }

    private bool PassesWithoutSoftRules(
        CoreAgent agent, SwapChainProbeItem item, IReadOnlyList<CoreToken> others, SlotRelaxation rung)
        => Valid(agent with { MinRestDays = 0, MaxWorkDays = 0 }, item, others, rung)
            && !WeeklyRestDayGuard.Violates(agent, item.Date, others, _context, item.StartAt, item.EndAt);

    private bool Valid(CoreAgent agent, SwapChainProbeItem item, IReadOnlyList<CoreToken> others, SlotRelaxation rung)
        => SlotConstraintFilter.IsValidAssignment(
            agent, item.Date, item.ShiftTypeIndex, item.ShiftRefId, item.Hours,
            _context, others, item.StartAt, item.EndAt, rung);

    private SwapChainProbeRule Classify(
        CoreAgent agent, SwapChainProbeItem item, IReadOnlyList<CoreToken> others, SlotRelaxation rung, bool packageRestMayShorten)
    {
        if (SlotConstraintFilter.HasHardTemporalCollision(agent.Id, item.StartAt, item.EndAt, _context, others))
        {
            return SwapChainProbeRule.Collision;
        }

        if (WeeklyRestDayGuard.Violates(agent, item.Date, others, _context, item.StartAt, item.EndAt))
        {
            return SwapChainProbeRule.WeeklyRestDays;
        }

        if (SlotConstraintFilter.ViolatesAbsoluteVeto(
                agent, item.Date, item.ShiftTypeIndex, item.ShiftRefId, _context, others, item.StartAt, item.EndAt))
        {
            return SwapChainProbeRule.DayVeto;
        }

        if (WeeklyHoursGuard.Violates(agent, item.Date, item.Hours, others, _context))
        {
            return SwapChainProbeRule.WeeklyHours;
        }

        if (TouchesAnotherShift(agent.Id, item, others))
        {
            return SwapChainProbeRule.MinPause;
        }

        var basis = packageRestMayShorten ? agent with { MinRestDays = 0 } : agent;
        if (Valid(basis with { MinRestHours = double.Epsilon }, item, others, rung))
        {
            return SwapChainProbeRule.MinPause;
        }

        if (!packageRestMayShorten && Valid(agent with { MinRestDays = 0 }, item, others, rung))
        {
            return SwapChainProbeRule.PackageRest;
        }

        if (Valid(basis with { MaxWorkDays = 0 }, item, others, rung))
        {
            return SwapChainProbeRule.BlockIdeal;
        }

        if (Valid(basis with { MaxWorkDays = 0, MaxConsecutiveDays = int.MaxValue }, item, others, rung))
        {
            return SwapChainProbeRule.ConsecutiveDaysCap;
        }

        return Valid(basis with { MaxDailyHours = double.MaxValue }, item, others, rung)
            ? SwapChainProbeRule.DailyHours
            : SwapChainProbeRule.SeveralOrOther;
    }

    private bool TouchesAnotherShift(string agentId, SwapChainProbeItem item, IReadOnlyList<CoreToken> others)
        => others.Any(token => token.AgentId == agentId && Touches(token.StartAt, token.EndAt, item))
            || _context.LockedWorks.Concat(_context.BoundaryLockedWorks)
                .Any(work => work.AgentId == agentId && Touches(work.StartAt, work.EndAt, item))
            || _context.ExistingWorkBlockers.Concat(_context.BoundaryExistingWorkBlockers)
                .Any(work => work.AgentId == agentId && Touches(work.StartAt, work.EndAt, item));

    private static bool Touches(DateTime start, DateTime end, SwapChainProbeItem item)
        => end == item.StartAt || start == item.EndAt;

    private HashSet<(string AgentId, Guid ShiftRefId, DateOnly Date)> SoftDeviationsOf(CoreScenario baseline)
    {
        var keys = new HashSet<(string AgentId, Guid ShiftRefId, DateOnly Date)>();
        foreach (var token in baseline.Tokens)
        {
            var agent = AgentOf(token.AgentId);
            if (agent is null)
            {
                continue;
            }

            var item = SwapChainProbeItem.ForHandedOn(token);
            var others = Without(baseline.Tokens, token);
            if (!Valid(agent, item, others, SlotRelaxation.None)
                && PassesWithoutSoftRules(agent, item, others, SlotRelaxation.None))
            {
                keys.Add((token.AgentId, token.ShiftRefId, token.Date));
            }
        }

        return keys;
    }
}
