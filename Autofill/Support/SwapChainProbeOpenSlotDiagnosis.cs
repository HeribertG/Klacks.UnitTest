// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// MEASUREMENT, not production code: explains, rule by rule, why the swap-chain probe closes an open slot of a finished
/// plan neither with one move nor with two. For every open slot it counts the refusals of every direct taker, of every
/// first move of a two-move chain (an employee takes the slot and hands on one of his unlocked shifts) and of every
/// receiver of such a handed-on shift, and it lists every first move that hands on a shift within one day of the slot
/// with the verdict of each receiver by name. Uses exactly the admission the probe uses, so a refusal named here is the
/// refusal that stopped the search. Chains of three or four moves are not broken down.
/// </summary>
public static class SwapChainProbeOpenSlotDiagnosis
{
    private const int NamedReliefDayDistance = 1;

    private const string Refused = "refused ";

    private const string Readmission = "re-admission of the mover's other shifts refused ";

    private const string Admitted = "admitted";

    private const string ReceiverAdmitted = "admitted (two-move chain exists)";

    /// <summary>The diagnosis of every open slot of the plan as report lines.</summary>
    /// <param name="plan">Finished plan of the engine</param>
    /// <param name="context">Context the plan was produced from</param>
    /// <param name="variant">Rule setting the refusals are judged under</param>
    public static IReadOnlyList<string> Describe(CoreScenario plan, CoreWizardContext context, SwapChainProbeVariant variant)
    {
        var admission = new SwapChainProbeAdmission(context, variant.PackageRestMayShorten, plan);
        var evaluation = EvaluationContext.For(context);
        var tokens = plan.Tokens;
        var lines = new List<string>();
        foreach (var key in SwapChainProbe.MissingAssignments(tokens, context).Distinct())
        {
            var prefix = "swap-chain probe diagnosis [" + variant.Label + "] open slot "
                + SwapChainProbe.Label(context, key.ShiftRefId, key.Date) + ": ";
            if (!evaluation.SlotsByKey.TryGetValue(key, out var slot))
            {
                lines.Add(prefix + "not in the slot list, the probe cannot search it");
                continue;
            }

            lines.AddRange(DescribeSlot(prefix, SwapChainProbeItem.ForOpenSlot(key.ShiftRefId, key.Date, slot), tokens, context, admission, variant));
        }

        return lines;
    }

    private static List<string> DescribeSlot(
        string prefix,
        SwapChainProbeItem item,
        List<CoreToken> tokens,
        CoreWizardContext context,
        SwapChainProbeAdmission admission,
        SwapChainProbeVariant variant)
    {
        var direct = new SwapChainProbeTally();
        var firstMoves = new SwapChainProbeTally();
        var receivers = new SwapChainProbeTally();
        var nearby = new List<string>();
        foreach (var agent in Candidates(context, tokens, item, excluded: null))
        {
            var refusedBefore = admission.Inadmissible(agent, tokens, variant.OpenSlot);
            direct.Add(DirectVerdict(admission, agent, item, tokens, refusedBefore, variant.OpenSlot), agent.Id);

            foreach (var relief in tokens.Where(t => !t.IsLocked && string.Equals(t.AgentId, agent.Id, StringComparison.Ordinal)))
            {
                var handedOn = agent.Id + " hands on " + SwapChainProbe.Label(context, relief.ShiftRefId, relief.Date);
                var working = SwapChainProbeAdmission.Without(tokens, relief);
                var verdict = DirectVerdict(admission, agent, item, working, refusedBefore, variant.OpenSlot);
                firstMoves.Add(verdict, handedOn);

                var receiverVerdicts = new List<string>();
                if (verdict == Admitted)
                {
                    working.Add(admission.TokenFor(item, agent));
                    var handedOnItem = SwapChainProbeItem.ForHandedOn(relief);
                    foreach (var receiver in Candidates(context, working, handedOnItem, agent.Id))
                    {
                        var receiverBefore = admission.Inadmissible(receiver, working, variant.Receiver);
                        var receiverVerdict = DirectVerdict(admission, receiver, handedOnItem, working, receiverBefore, variant.Receiver);
                        receiverVerdict = receiverVerdict == Admitted ? ReceiverAdmitted : receiverVerdict;
                        receivers.Add(receiverVerdict, handedOn + " to " + receiver.Id);
                        receiverVerdicts.Add(receiver.Id + " " + receiverVerdict);
                    }
                }

                if (Math.Abs(relief.Date.DayNumber - item.Date.DayNumber) <= NamedReliefDayDistance)
                {
                    nearby.Add(prefix + "nearby first move: " + handedOn + ": " + verdict
                        + (receiverVerdicts.Count == 0 ? string.Empty : "; receivers: " + string.Join(", ", receiverVerdicts)));
                }
            }
        }

        var lines = new List<string>
        {
            prefix + "direct takers: " + direct.Counts(),
            prefix + "two-move chains, first move (taker hands on one own unlocked shift): " + firstMoves.Counts(),
            prefix + "two-move chains, second move (receiver of the handed-on shift): " + receivers.Counts(),
        };
        lines.AddRange(direct.Examples().Select(example => prefix + "direct example: " + example));
        lines.AddRange(receivers.Examples().Select(example => prefix + "receiver example: " + example));
        lines.AddRange(nearby);
        return lines;
    }

    private static IEnumerable<CoreAgent> Candidates(
        CoreWizardContext context, IReadOnlyList<CoreToken> tokens, SwapChainProbeItem item, string? excluded)
    {
        var holders = tokens
            .Where(t => t.ShiftRefId == item.ShiftRefId && t.Date == item.Date)
            .Select(t => t.AgentId)
            .ToHashSet(StringComparer.Ordinal);
        return context.Agents.Where(a => !a.IsStandIn
            && !holders.Contains(a.Id)
            && !string.Equals(a.Id, excluded, StringComparison.Ordinal));
    }

    private static string DirectVerdict(
        SwapChainProbeAdmission admission,
        CoreAgent agent,
        SwapChainProbeItem item,
        IReadOnlyList<CoreToken> others,
        HashSet<CoreToken> refusedBefore,
        SlotRelaxation rung)
    {
        var refused = admission.Reject(agent, item, others, rung);
        if (refused is not null)
        {
            return Refused + refused.Value;
        }

        var placed = new List<CoreToken>(others) { admission.TokenFor(item, agent) };
        var added = admission.FirstNewInadmissible(refusedBefore, agent, placed, rung);
        return added is null ? Admitted : Readmission + added.Value.Rule;
    }
}
