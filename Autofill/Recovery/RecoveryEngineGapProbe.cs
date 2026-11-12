// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: lets the productive recovery engine (<see cref="LocalRepairEngine"/>, absence cover
/// with swap chains) loose on the open slots of a finished autofill plan, as a counter-check for the autofill's own chain
/// search. Every open head is repaired on its own on the unchanged plan (standard ruleset with chains of up to three
/// moves, then up to five moves), then all open heads together in one run. The engine only repairs the works of an
/// absent person, so each open head is handed over as the work of a phantom holder outside the candidate pool. Every
/// CLOSED chain is replayed on the plan and re-checked by the autofill's own assignment filter; when the filter refuses
/// it, the receiving persons of the refused chain are gated for exactly the work they took
/// (<see cref="RecoverySnapshot.Excluding"/>) and the engine repairs the same slot again, round by round, until a chain
/// passes the filter, the slot stays open or the round cap is reached. A slot without an autofill-legal chain is repaired
/// once more with ten times the deep-chain probe budget. Nothing is asserted and nothing is thrown; every line starts
/// with <see cref="RecoveryProbeFormats.Prefix"/>, and the output is deterministic (no clock, no random source).
/// </summary>
public static class RecoveryEngineGapProbe
{
    public const int DeepChainMoves = 5;

    public const int BudgetFactor = 10;

    public const int MaxRetryRounds = 100;

    private const int UnbudgetedChainMoves = 2;

    private const string Standard = "standard";

    private const string Deep = "deep";

    private const string BudgetSuffix = "-budget-x10";

    private static readonly IReadOnlyList<(string Name, Ruleset Rules)> Rulesets =
    [
        (Standard, Ruleset.Default),
        (Deep, Ruleset.Default with { MaxSwapChainDepth = DeepChainMoves }),
    ];

    private static readonly string[] StaticAssumptions =
    [
        "open slot = work of a phantom holder (AbsenceEvent on the phantom); the phantom is not in the candidate pool, so it is no candidate, no chain member, no budget share and no rule row; its only trace is FROM=" + RecoveryProbeFormats.OpenSlotHolder + " on the cover move",
        "candidate pool = every non-stand-in agent of the context, all in-group (IsInGroup=true); no group tree, sister groups or cross-group pool are derivable, so the cross-group tiers never occur",
        "engine ids are assigned in roster order (context.Agents index); the engine's Guid tie-break and the order in which the deep-chain budget is spent follow that order",
        "every slot is critical; no on-call days, no sealed days beyond the autofill's break blockers, no lead/trail/extra paid hours, no swap-locked works",
        "existing works and boundary works (locked and unlocked) are locked occupancy: the autofill never moves them either; the engine counts them toward the daily cap too, the autofill counts plan tokens only (engine stricter there)",
        "firstMovableDay = first day of the period (default would be the slot's own day); reliefs only within ReliefReachDays around the slot day (engine rule; the autofill chain probe has no such reach)",
        "keyword restrictions are applied as hard gates in both keyword modes (stricter than the autofill's Soft mode, which may break a wish for coverage on its last rung)",
        "the engine does not know the autofill rules MinRestDays (package rest), weekly rest days, MaxWorkDays ideal and MaxOptimalGap; RECHECK replays each CLOSED chain through the autofill filter on rung All and names the refusing rule",
        "the engine stops at the cheapest cover: a direct cover beats any chain, and chains longer than two moves are only searched when nothing shorter exists; so a refused cover can hide an autofill-legal chain, which is why refused chains are retried with the refused receivers gated",
        "RETRY gates a receiving person for the exact (shift, day) it took in a refused chain, in every role; a chain in which that person takes the same work while giving another away is therefore not tried again, so OPEN after retries is no proof that no autofill-legal chain exists; a refusal only on a person who gives a work away cannot be gated and ends the retry as INCONCLUSIVE",
        "the engine does not expose whether the deep-chain probe budget was spent (ChainSearch keeps it private); BUDGET exhausted=unknown unless the run with ten times the budget finds an autofill-legal chain the normal budget did not (then true, inferred); chains of at most two moves are never budgeted",
    ];

    /// <summary>The probe report for the open slots of a finished plan.</summary>
    /// <param name="plan">Finished autofill plan, never modified</param>
    /// <param name="context">Context the plan was produced from</param>
    public static IReadOnlyList<string> Describe(CoreScenario plan, CoreWizardContext context)
    {
        var lines = new List<string>();
        try
        {
            DescribeInto(lines, plan, context);
        }
        catch (Exception exception)
        {
            lines.Add(Error(exception));
        }

        return lines;
    }

    private static void DescribeInto(List<string> lines, CoreScenario plan, CoreWizardContext context)
    {
        var problems = new List<string>();
        var openSlots = RecoveryProbeOpenSlotFinder.Find(plan.Tokens, context, problems);
        var input = RecoveryProbeInput.Build(plan, context);
        var probe = new RecoveryProbeSlotRun(new LocalRepairEngine(), input, new RecoveryProbeAutofillRecheck(plan, context, input), context, lines);

        WriteHeader(lines, plan, context, input, openSlots.Count, problems);

        var results = Rulesets
            .SelectMany(r => new[] { r.Name, r.Name + BudgetSuffix })
            .ToDictionary(name => name, _ => new List<RecoveryProbeSlotResult>());
        for (var index = 0; index < openSlots.Count; index++)
        {
            var slot = openSlots[index];
            var number = "#" + Count(index + 1);
            lines.Add(Line($"SLOT {number} {SlotLabel(context, slot.ShiftRefId, slot.Date, slot.StartAt, slot.EndAt)} hours={Format(slot.Hours)}"));

            var budgetVerdicts = new List<string>();
            foreach (var (name, rules) in Rulesets)
            {
                var result = probe.Run(slot, number, name, rules);
                results[name].Add(result);
                if (result.LegalClosed)
                {
                    budgetVerdicts.Add($"{name}:{(result.LegalMoves <= UnbudgetedChainMoves ? "false" : "unknown")}");
                    continue;
                }

                var wide = probe.Run(slot, number, name + BudgetSuffix, Widened(rules));
                results[name + BudgetSuffix].Add(wide);
                budgetVerdicts.Add($"{name}:{(wide.LegalClosed ? "true(inferred)" : "unknown")} {name + BudgetSuffix}={wide.Verdict}");
            }

            lines.Add(Line($"BUDGET {number} exhausted={Aggregate(budgetVerdicts)} per-ruleset: {string.Join("; ", budgetVerdicts)}"));
        }

        foreach (var (name, rules) in Rulesets)
        {
            probe.RunAll(openSlots, name, rules);
        }

        foreach (var (name, _) in Rulesets)
        {
            WriteSummary(lines, name, openSlots.Count, results[name]);
            WriteSummary(lines, name + BudgetSuffix, results[name + BudgetSuffix].Count, results[name + BudgetSuffix]);
        }
    }

    private static void WriteHeader(
        List<string> lines,
        CoreScenario plan,
        CoreWizardContext context,
        RecoveryProbeInput input,
        int openCount,
        List<string> problems)
    {
        lines.Add(Line(
            $"HEADER engine={typeof(LocalRepairEngine).FullName} period={context.PeriodFrom.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture)}.."
            + $"{context.PeriodUntil.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture)} agents={Count(context.Agents.Count)} "
            + $"candidates={Count(input.CandidateCount)} tokens={Count(plan.Tokens.Count)} openSlots={Count(openCount)} keywordMode={context.KeywordMode} "
            + $"maxRetryRounds={Count(MaxRetryRounds)}"));
        foreach (var (name, rules) in Rulesets)
        {
            lines.Add(Line($"RULESET name={name} maxChainMoves={Count(rules.MaxSwapChainDepth)} deepChainProbeBudget={Count(rules.DeepChainProbeBudget)} {rules}"));
            lines.Add(Line($"RULESET name={name + BudgetSuffix} (only for slots {name} leaves without an autofill-legal chain) {Widened(rules)}"));
        }

        lines.Add(Line(
            $"RULE context defaults: SchedulingMaxWeeklyHours={context.SchedulingMaxWeeklyHours.ToString(CultureInfo.InvariantCulture)} SchedulingMinPauseHours={context.SchedulingMinPauseHours.ToString(CultureInfo.InvariantCulture)} "
            + $"SchedulingMaxConsecutiveDays={Count(context.SchedulingMaxConsecutiveDays)} SchedulingMaxDailyHours={context.SchedulingMaxDailyHours.ToString(CultureInfo.InvariantCulture)}; "
            + "an agent value of 0 falls back to these, exactly like the autofill (the engine itself would read 0 as unconstrained)"));
        lines.AddRange(input.RuleLines.Select(Line));
        lines.AddRange(StaticAssumptions.Select(a => Line("ASSUMPTION " + a)));
        lines.AddRange(input.AssumptionLines.Select(a => Line("ASSUMPTION " + a)));
        lines.AddRange(problems.Select(p => Line("ERROR SlotTimes: " + p)));
    }

    private static void WriteSummary(List<string> lines, string name, int openCount, IReadOnlyList<RecoveryProbeSlotResult> results)
    {
        var engineClosed = results.Where(r => r.EngineClosed).ToList();
        var legal = results.Where(r => r.LegalClosed).ToList();
        lines.Add(Line(
            $"SUMMARY ruleset={name} single-slot runs: open={Count(openCount)} "
            + $"engine-closed-first-round={Count(engineClosed.Count)} ({ByMoves(engineClosed.Select(r => r.EngineMoves))}) "
            + $"refused-by-autofill-filter-first-round={Count(engineClosed.Count(r => r.EngineRefused))} "
            + $"autofill-legal-closed={Count(legal.Count)} ({ByMoves(legal.Select(r => r.LegalMoves))}) "
            + $"open-after-retries={Count(results.Count(r => r.Verdict.StartsWith("OPEN", StringComparison.Ordinal)))} "
            + $"inconclusive={Count(results.Count(r => r.Verdict.StartsWith("INCONCLUSIVE", StringComparison.Ordinal)))}"));
    }

    private static Ruleset Widened(Ruleset rules) => rules with { DeepChainProbeBudget = rules.DeepChainProbeBudget * BudgetFactor };

    private static string ByMoves(IEnumerable<int> moves)
        => string.Join(" ", moves.GroupBy(m => m).OrderBy(g => g.Key).Select(g => $"moves{Count(g.Key)}={Count(g.Count())}"));

    private static string Aggregate(IReadOnlyList<string> verdicts)
    {
        if (verdicts.Any(v => v.Contains(":true(inferred)", StringComparison.Ordinal)))
        {
            return "true";
        }

        return verdicts.All(v => v.EndsWith(":false", StringComparison.Ordinal)) ? "false" : "unknown";
    }

    /// <summary>A slot as the report names it: day, shift name, times and shift id.</summary>
    /// <param name="context">Context holding the shift definitions</param>
    /// <param name="shiftRefId">Shift definition</param>
    /// <param name="date">Day of the slot</param>
    /// <param name="startAt">Start instant</param>
    /// <param name="endAt">End instant</param>
    internal static string SlotLabel(CoreWizardContext context, Guid shiftRefId, DateOnly date, DateTime startAt, DateTime endAt)
    {
        var name = EvaluationContext.For(context).SlotsByKey.TryGetValue((shiftRefId, date), out var shift) ? shift.Name : "?";
        return $"{date.ToString(RecoveryProbeFormats.Date, CultureInfo.InvariantCulture)} {name} "
            + $"{startAt.ToString(RecoveryProbeFormats.Time, CultureInfo.InvariantCulture)}-{endAt.ToString(RecoveryProbeFormats.Time, CultureInfo.InvariantCulture)} [{shiftRefId}]";
    }

    /// <summary>The moves of a proposal as one text, in engine order.</summary>
    /// <param name="context">Context holding the shift definitions</param>
    /// <param name="input">Engine-id to agent-id mapping</param>
    /// <param name="deltas">Moves in engine order</param>
    internal static string Chain(CoreWizardContext context, RecoveryProbeInput input, IReadOnlyList<CellDelta> deltas)
    {
        var moves = deltas.Select((delta, index) =>
            $"move{Count(index + 1)}: {input.NameOf(delta.ToAgentId)} takes "
            + $"{SlotLabel(context, delta.ShiftId ?? Guid.Empty, delta.Date, delta.StartAt, delta.EndAt)} from {input.NameOf(delta.FromAgentId)} ({delta.Tier})");
        return string.Join(" | ", moves) + " (engine order: hand-ons first, the cover of the open slot last)";
    }

    internal static string Format(decimal value) => value.ToString(RecoveryProbeFormats.Hours, CultureInfo.InvariantCulture);

    internal static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Line(string text) => RecoveryProbeFormats.Prefix + text;

    internal static string Error(Exception exception)
        => Line($"ERROR {exception.GetType().Name}: {exception.Message}");
}
