// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleRecovery.Engine;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.Autofill.Recovery;

/// <summary>
/// MEASUREMENT, not production code: one engine repair per open slot on the unchanged plan, re-checked by the autofill
/// filter and retried with the refused receivers gated until a chain passes, the slot stays open or the round cap of
/// <see cref="RecoveryEngineGapProbe.MaxRetryRounds"/> is reached; plus the one repair of all open slots together.
/// Writes its lines into <paramref name="lines"/> and never throws.
/// </summary>
/// <param name="engine">The productive recovery engine</param>
/// <param name="input">Snapshot pieces of the unchanged plan</param>
/// <param name="recheck">Replays a proposal through the autofill filter</param>
/// <param name="context">Context the plan was produced from, for the slot labels</param>
/// <param name="lines">Report the lines are appended to</param>
public sealed class RecoveryProbeSlotRun(
    LocalRepairEngine engine,
    RecoveryProbeInput input,
    RecoveryProbeAutofillRecheck recheck,
    CoreWizardContext context,
    List<string> lines)
{
    /// <summary>Repairs one open slot, retrying refused chains with their receivers gated.</summary>
    /// <param name="slot">Open slot</param>
    /// <param name="number">Slot number as printed ("#3")</param>
    /// <param name="name">Ruleset name as printed</param>
    /// <param name="rules">Ruleset of the engine</param>
    public RecoveryProbeSlotResult Run(RecoveryProbeOpenSlot slot, string number, string name, Ruleset rules)
    {
        IReadOnlyList<RecoveryProbeOpenSlot> slots = [slot];
        var gated = new List<IneligibleKey>();
        RecoveryProbeSlotResult? first = null;
        try
        {
            for (var round = 0; round <= RecoveryEngineGapProbe.MaxRetryRounds; round++)
            {
                var tag = round == 0
                    ? $"RESULT {number} ruleset={name}"
                    : $"RETRY {number} ruleset={name} round={RecoveryEngineGapProbe.Count(round)} gated={RecoveryEngineGapProbe.Count(gated.Count)}";
                var snapshot = input.Snapshot(slots).Excluding([], gated);
                var proposal = engine.Repair(snapshot, new AbsenceEvent(RecoveryProbeInput.PhantomId, [slot.Date]), rules);
                if (proposal.Uncovered.Count > 0 || proposal.Deltas.Count == 0)
                {
                    var reasons = proposal.Uncovered.Count == 0
                        ? "empty proposal"
                        : string.Join(",", proposal.Uncovered.Select(u => $"{u.Reason}(critical={u.IsCritical})"));
                    lines.Add(RecoveryEngineGapProbe.Line($"{tag} OPEN reason={reasons} (the engine names no per-candidate or per-rule refusals)"));
                    return Finish(number, name, first, false, 0, $"OPEN after {RecoveryEngineGapProbe.Count(round)} retry rounds reason={reasons}");
                }

                var moves = proposal.Deltas.Count;
                lines.Add(RecoveryEngineGapProbe.Line(
                    $"{tag} CLOSED moves={RecoveryEngineGapProbe.Count(moves)} tier={proposal.HighestTier} harmonyDelta={RecoveryEngineGapProbe.Count(proposal.HarmonyDelta)} "
                    + $"chain: {RecoveryEngineGapProbe.Chain(context, input, proposal.Deltas)}"));
                var verdict = recheck.Verdict(proposal.Deltas, slots);
                lines.Add(RecoveryEngineGapProbe.Line($"RECHECK {number} ruleset={name} round={RecoveryEngineGapProbe.Count(round)} autofill-filter={verdict.Text}"));
                first ??= new RecoveryProbeSlotResult(true, moves, !verdict.IsOk, false, 0, string.Empty);
                if (verdict.IsOk)
                {
                    return Finish(number, name, first, true, moves, $"CLOSED moves={RecoveryEngineGapProbe.Count(moves)} after {RecoveryEngineGapProbe.Count(round)} retry rounds");
                }

                var fresh = proposal.Deltas
                    .Where(d => d.ShiftId is Guid id && id != Guid.Empty && verdict.RefusedAgentIds.Contains(input.NameOf(d.ToAgentId)))
                    .Select(d => new IneligibleKey(d.ToAgentId, d.ShiftId!.Value, d.Date))
                    .Where(k => !gated.Contains(k))
                    .ToList();
                if (fresh.Count == 0)
                {
                    return Finish(number, name, first, false, 0,
                        "INCONCLUSIVE (the refusal lies only on a person who gives a work away, or the chain could not be replayed; an eligibility gate cannot express it)");
                }

                gated.AddRange(fresh);
            }

            return Finish(number, name, first, false, 0, "INCONCLUSIVE (round cap reached)");
        }
        catch (Exception exception)
        {
            lines.Add(RecoveryEngineGapProbe.Error(exception) + $" (slot {number}, ruleset {name})");
            return Finish(number, name, first, false, 0, "INCONCLUSIVE (error)");
        }
    }

    /// <summary>Repairs all open slots in one engine run and re-checks the whole proposal once.</summary>
    /// <param name="openSlots">Every open slot of the plan</param>
    /// <param name="name">Ruleset name as printed</param>
    /// <param name="rules">Ruleset of the engine</param>
    public void RunAll(IReadOnlyList<RecoveryProbeOpenSlot> openSlots, string name, Ruleset rules)
    {
        if (openSlots.Count == 0)
        {
            lines.Add(RecoveryEngineGapProbe.Line($"ALL ruleset={name} nothing open"));
            return;
        }

        try
        {
            var dates = openSlots.Select(s => s.Date).Distinct().OrderBy(d => d.DayNumber).ToList();
            var proposal = engine.Repair(input.Snapshot(openSlots), new AbsenceEvent(RecoveryProbeInput.PhantomId, dates), rules);
            var closed = proposal.Deltas.Select(d => d.OptionId).Distinct().Count();
            lines.Add(RecoveryEngineGapProbe.Line(
                $"ALL ruleset={name} open={RecoveryEngineGapProbe.Count(openSlots.Count)} closed={RecoveryEngineGapProbe.Count(closed)} "
                + $"still-open={RecoveryEngineGapProbe.Count(proposal.Uncovered.Count)} moves={RecoveryEngineGapProbe.Count(proposal.Deltas.Count)} tier={proposal.HighestTier} "
                + $"objective={proposal.Objective} harmonyDelta={RecoveryEngineGapProbe.Count(proposal.HarmonyDelta)} "
                + "(one engine run, lookahead beam over all open slots, slots taken in date order, no retry)"));
            foreach (var option in proposal.Deltas.GroupBy(d => d.OptionId).OrderBy(g => g.Key))
            {
                lines.Add(RecoveryEngineGapProbe.Line(
                    $"ALL-CHAIN ruleset={name} option={RecoveryEngineGapProbe.Count(option.Key)} moves={RecoveryEngineGapProbe.Count(option.Count())} "
                    + $"chain: {RecoveryEngineGapProbe.Chain(context, input, option.ToList())}"));
            }

            foreach (var uncovered in proposal.Uncovered)
            {
                var slot = openSlots.FirstOrDefault(s => s.ShiftRefId == uncovered.ShiftId && s.Date == uncovered.Date);
                var label = slot is null
                    ? $"{uncovered.Date} [{uncovered.ShiftId}]"
                    : RecoveryEngineGapProbe.SlotLabel(context, slot.ShiftRefId, slot.Date, slot.StartAt, slot.EndAt);
                lines.Add(RecoveryEngineGapProbe.Line($"ALL-OPEN ruleset={name} {label} reason={uncovered.Reason}"));
            }

            lines.Add(RecoveryEngineGapProbe.Line($"ALL-RECHECK ruleset={name} autofill-filter={recheck.Verdict(proposal.Deltas, openSlots).Text}"));
        }
        catch (Exception exception)
        {
            lines.Add(RecoveryEngineGapProbe.Error(exception) + $" (all slots, ruleset {name})");
        }
    }

    private RecoveryProbeSlotResult Finish(
        string number, string name, RecoveryProbeSlotResult? first, bool legalClosed, int legalMoves, string verdict)
    {
        lines.Add(RecoveryEngineGapProbe.Line($"AUTOFILL-LEGAL {number} ruleset={name} {verdict}"));
        return (first ?? new RecoveryProbeSlotResult(false, 0, false, false, 0, string.Empty)) with
        {
            LegalClosed = legalClosed,
            LegalMoves = legalMoves,
            Verdict = verdict,
        };
    }
}
