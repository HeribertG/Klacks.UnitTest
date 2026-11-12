// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Positive control of <see cref="SwapChainProbe"/>: a probe that reports "no chain" proves nothing as long as nobody
/// has seen it find one. The control opens a slot of a finished plan in a way that is known to close with two moves:
/// on one day employee A gives up shift X and takes over shift Y of employee B, so X is open and B is free. Handing Y
/// back to B and taking X again restores the plan the engine produced, so a two-move chain exists by construction.
/// Only constellations in which nobody can take X directly are used, so one move cannot answer them. Locked shifts
/// are never opened or taken.
/// <para>
/// The probe runs with the engine's plan as its baseline: a soft deviation (package rest, block ideal) the engine's
/// plan held on X or Y does not count as new when the restore recreates it. Without that baseline the control plan
/// itself would be the reference, in which the deviation is gone, and the restore would be refused for recreating a
/// deviation the engine had accepted. The control reports both readings of the restore chain, rule by rule, so the
/// cause of every slot it does not close is visible.
/// </para>
/// </summary>
public static class SwapChainProbeSelfCheck
{
    /// <summary>Constellations the control runs at most.</summary>
    public const int MaxControls = 10;

    private const int TwoMovesIndex = 1;

    private const string MoveOne = "move 1 (A takes X back): ";

    private const string MoveOneReadmission = "move 1 re-admission of A's other shifts: ";

    private const string MoveTwo = "move 2 (B takes Y back): ";

    private const string MoveTwoReadmission = "move 2 re-admission of B's other shifts: ";

    private const string RestoreAdmissible = "restore chain admissible";

    private const string SlotUnknown = "open slot not in the slot list";

    private const string EmployeeUnknown = "employee not in the context";

    private const string ExamplePrefix = "swap-chain probe self-check example [";

    /// <summary>Runs the control on the plan and returns the result as report lines.</summary>
    /// <param name="plan">Finished plan of the engine</param>
    /// <param name="context">Context the plan was produced from</param>
    public static IReadOnlyList<string> Run(CoreScenario plan, CoreWizardContext context)
    {
        var variant = SwapChainProbeVariant.IdealRelaxedOnEveryMove;
        var withoutBaseline = new SwapChainProbeAdmission(context, variant.PackageRestMayShorten, baseline: null);
        var withBaseline = new SwapChainProbeAdmission(context, variant.PackageRestMayShorten, plan);
        var restoreWithoutBaseline = new SwapChainProbeTally();
        var notClosed = new SwapChainProbeTally();
        var built = 0;
        var refilled = 0;
        var refilledWithTwoMoves = 0;
        var cutOff = 0;

        foreach (var control in Controls(plan, context, variant))
        {
            built++;
            var (rawLabel, rawExample) = RestoreVerdict(control, context, withoutBaseline, variant);
            restoreWithoutBaseline.Add(rawLabel, rawExample);

            var result = SwapChainProbe.Measure(control.Plan, context, variant, plan);
            if (Holders(result.Plan.Tokens, control.Opened) <= Holders(control.Plan.Tokens, control.Opened))
            {
                cutOff += result.BudgetExhausted;
                var (label, example) = RestoreVerdict(control, context, withBaseline, variant);
                notClosed.Add(label, example);
                continue;
            }

            refilled++;
            if (result.ClosedByMoves[TwoMovesIndex] > 0)
            {
                refilledWithTwoMoves++;
            }
        }

        var lines = new List<string>
        {
            "swap-chain probe self-check: " + Number(built) + " slot(s) opened so that two moves close them and one "
                + "move cannot; the probe closed " + Number(refilled) + " of them, " + Number(refilledWithTwoMoves)
                + " in a run that used a two-move chain"
                + (built == 0 ? " (no such constellation in this plan, so the control says nothing)" : string.Empty)
                + "; baseline = the engine's plan, which holds " + Number(withBaseline.ToleratedCount)
                + " assignment(s) breaking package rest or block ideal that a move may recreate",
            "swap-chain probe self-check: restore chain of every control judged WITHOUT the baseline (control plan as "
                + "reference, the probe's reading before this fix): " + restoreWithoutBaseline.Counts(),
            "swap-chain probe self-check: controls the probe did not close, restore chain judged WITH the baseline: "
                + notClosed.Counts() + "; open-slot searches cut off by the budget in those runs " + Number(cutOff),
        };
        lines.AddRange(restoreWithoutBaseline.Examples().Select(example => ExamplePrefix + "without baseline] " + example));
        lines.AddRange(notClosed.Examples().Select(example => ExamplePrefix + "not closed] " + example));
        return lines;
    }

    private static (string Label, string Example) RestoreVerdict(
        SwapChainProbeControl control, CoreWizardContext context, SwapChainProbeAdmission admission, SwapChainProbeVariant variant)
    {
        var example = Describe(control, context);
        var a = admission.AgentOf(control.Opened.AgentId);
        var b = admission.AgentOf(control.Taken.AgentId);
        if (a is null || b is null)
        {
            return (EmployeeUnknown, example);
        }

        if (!EvaluationContext.For(context).SlotsByKey.TryGetValue((control.Opened.ShiftRefId, control.Opened.Date), out var slot))
        {
            return (SlotUnknown, example);
        }

        var openItem = SwapChainProbeItem.ForOpenSlot(control.Opened.ShiftRefId, control.Opened.Date, slot);
        var tokens = control.Plan.Tokens;
        var working = SwapChainProbeAdmission.Without(tokens, control.Moved);
        var refused = admission.Reject(a, openItem, working, variant.OpenSlot);
        if (refused is not null)
        {
            return (MoveOne + refused.Value, example);
        }

        var beforeA = admission.Inadmissible(a, tokens, variant.OpenSlot);
        working.Add(admission.TokenFor(openItem, a));
        var added = admission.FirstNewInadmissible(beforeA, a, working, variant.OpenSlot);
        if (added is not null)
        {
            return (MoveOneReadmission + added.Value.Rule, example + "; refused shift " + TokenLabel(added.Value.Token, context));
        }

        var handedOn = SwapChainProbeItem.ForHandedOn(control.Moved);
        refused = admission.Reject(b, handedOn, working, variant.Receiver);
        if (refused is not null)
        {
            return (MoveTwo + refused.Value, example);
        }

        var beforeB = admission.Inadmissible(b, working, variant.Receiver);
        var restored = new List<CoreToken>(working) { admission.TokenFor(handedOn, b) };
        added = admission.FirstNewInadmissible(beforeB, b, restored, variant.Receiver);
        return added is null
            ? (RestoreAdmissible, example)
            : (MoveTwoReadmission + added.Value.Rule, example + "; refused shift " + TokenLabel(added.Value.Token, context));
    }

    private static IEnumerable<SwapChainProbeControl> Controls(
        CoreScenario plan, CoreWizardContext context, SwapChainProbeVariant variant)
    {
        var people = context.Agents.Where(agent => !agent.IsStandIn).ToList();
        var yielded = 0;
        foreach (var day in plan.Tokens.Where(token => !token.IsLocked).GroupBy(token => token.Date).OrderBy(group => group.Key))
        {
            var dayTokens = day.ToList();
            foreach (var opened in dayTokens)
            {
                foreach (var taken in dayTokens)
                {
                    if (opened.ShiftRefId == taken.ShiftRefId
                        || opened.ShiftTypeIndex == taken.ShiftTypeIndex
                        || string.Equals(opened.AgentId, taken.AgentId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var moved = taken with { AgentId = opened.AgentId };
                    var tokens = plan.Tokens
                        .Where(token => !ReferenceEquals(token, opened) && !ReferenceEquals(token, taken))
                        .Append(moved)
                        .ToList();
                    if (SomebodyTakesItDirectly(opened, tokens, people, context, variant))
                    {
                        continue;
                    }

                    yield return new SwapChainProbeControl(
                        new CoreScenario
                        {
                            Id = plan.Id,
                            Tokens = tokens,
                            KeywordMode = plan.KeywordMode,
                            PlannedAgentIds = plan.PlannedAgentIds,
                        },
                        opened,
                        taken,
                        moved);

                    yielded++;
                    if (yielded >= MaxControls)
                    {
                        yield break;
                    }

                    break;
                }
            }
        }
    }

    private static bool SomebodyTakesItDirectly(
        CoreToken opened,
        List<CoreToken> tokens,
        List<CoreAgent> people,
        CoreWizardContext context,
        SwapChainProbeVariant variant)
    {
        var holders = tokens
            .Where(token => token.ShiftRefId == opened.ShiftRefId && token.Date == opened.Date)
            .Select(token => token.AgentId)
            .ToHashSet(StringComparer.Ordinal);
        return people.Any(agent => !holders.Contains(agent.Id)
            && SlotConstraintFilter.IsValidAssignment(
                agent, opened.Date, opened.ShiftTypeIndex, opened.ShiftRefId, opened.TotalHours,
                context, tokens, opened.StartAt, opened.EndAt, variant.OpenSlot));
    }

    private static string Describe(SwapChainProbeControl control, CoreWizardContext context)
        => "X = " + TokenLabel(control.Opened, context) + " opened; A = " + control.Opened.AgentId + " now holds Y = "
            + TokenLabel(control.Taken, context) + " of B = " + control.Taken.AgentId;

    private static string TokenLabel(CoreToken token, CoreWizardContext context)
        => SwapChainProbe.Label(context, token.ShiftRefId, token.Date) + " (" + token.AgentId + ")";

    private static int Holders(IReadOnlyList<CoreToken> tokens, CoreToken slot)
        => tokens.Count(token => token.ShiftRefId == slot.ShiftRefId && token.Date == slot.Date);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
