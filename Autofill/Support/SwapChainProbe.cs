// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using Klacks.ScheduleOptimizer.Constraints;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Constraints;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// MEASUREMENT, not production code: searches swap chains for the slots a finished plan leaves open and
/// answers how many of them close with one, two, three or four moves. A chain is: an employee takes
/// the open slot; when one of his own shifts is in the way he hands exactly that shift on to the next
/// employee, who may in turn hand one on. Every placement passes <see cref="SwapChainProbeAdmission"/> (the
/// engine's assignment filter plus the variant's package-rest setting), no employee appears twice in a chain,
/// every employee hands on at most one shift, locked shifts never move and stand-ins never take part.
/// <para>
/// The filter judges one placement. Taking a shift out of the middle of a block can leave the block's
/// two halves too close to each other, and the plan checker does not know the package rest. So after
/// every move the employee's own shifts are re-admitted one by one, and a move that makes any of them
/// inadmissible is discarded.
/// </para>
/// <para>
/// The re-admission compares SETS, not counts: a move is discarded as soon as one shift is inadmissible
/// that was admissible before, whatever else the move happens to repair. Locked shifts are re-admitted
/// too, because they can be the only witness of a rest that got too short. Existing works that are not
/// part of the plan cannot be re-admitted; a rule only they can see stays unchecked. A soft deviation (package
/// rest, block ideal) that the baseline plan already held on the same employee, shift and day never counts as new.
/// </para>
/// <para>
/// "No chain found" is not a proof that none exists: the search is cut off by a probe budget per slot,
/// takes the first chain it meets, and the slots are closed greedily in date order, so the counts are a lower bound.
/// </para>
/// </summary>
public static class SwapChainProbe
{
    /// <summary>Longest chain the measurement looks for.</summary>
    public const int MaxMoves = 4;

    /// <summary>Rule checks one open slot may cost before its search is cut off.</summary>
    public const long ProbeBudgetPerSlot = 200_000;

    /// <summary>Passes over the open slots; a later pass retries the slots an earlier chain may have freed.</summary>
    public const int MaxPasses = 3;

    private const string DayFormat = "MM-dd";

    /// <summary>
    /// Closes as many open slots of the plan as chains of up to <see cref="MaxMoves"/> moves allow and
    /// reports the count per chain length. The plan handed in is never modified.
    /// </summary>
    /// <param name="plan">Plan whose open slots are searched</param>
    /// <param name="context">Context the plan was produced from</param>
    /// <param name="variant">Rule setting of the measurement</param>
    /// <param name="baseline">The engine's own plan, whose soft deviations are tolerated; null means <paramref name="plan"/></param>
    public static SwapChainProbeResult Measure(
        CoreScenario plan, CoreWizardContext context, SwapChainProbeVariant variant, CoreScenario? baseline = null)
    {
        var checker = new TokenConstraintChecker();
        var legalityBefore = checker.CountViolationsSplit(plan, context).Legality;
        var admission = new SwapChainProbeAdmission(context, variant.PackageRestMayShorten, baseline ?? plan);
        var strictRest = variant.PackageRestMayShorten
            ? new SwapChainProbeAdmission(context, packageRestMayShorten: false, baseline ?? plan)
            : null;

        var tokens = plan.Tokens.ToList();
        var openBefore = MissingAssignments(tokens, context).Count;
        var closedByMoves = new int[MaxMoves];
        var chains = new List<string>();
        var exceptions = new List<int>();
        var exhaustedSlots = new HashSet<(Guid ShiftRefId, DateOnly Date)>();
        long probes = 0;

        var progress = true;
        for (var pass = 0; pass < MaxPasses && progress; pass++)
        {
            progress = false;
            exhaustedSlots.Clear();
            foreach (var key in MissingAssignments(tokens, context).Distinct().ToList())
            {
                var search = new Search(context, variant, admission);
                var found = search.Close(key, tokens);
                probes += search.Probes;
                if (found is null)
                {
                    if (search.Exhausted)
                    {
                        exhaustedSlots.Add(key);
                    }

                    continue;
                }

                exceptions.Add(strictRest is null ? 0 : PackageRestExceptions(strictRest, variant, tokens, found));
                tokens = found.Tokens;
                closedByMoves[found.Moves - 1]++;
                chains.Add(string.Join(" | ", found.Steps));
                progress = true;
            }
        }

        var after = new CoreScenario
        {
            Id = plan.Id,
            Tokens = tokens,
            KeywordMode = plan.KeywordMode,
            PlannedAgentIds = plan.PlannedAgentIds,
        };

        var stillOpen = MissingAssignments(tokens, context);
        return new SwapChainProbeResult(
            variant,
            openBefore,
            closedByMoves,
            stillOpen.Count,
            stillOpen.Count(exhaustedSlots.Contains),
            probes,
            legalityBefore,
            checker.CountViolationsSplit(after, context).Legality,
            chains,
            after,
            exceptions,
            admission.ToleratedCount);
    }

    /// <summary>Every missing assignment of the plan, one entry per missing head, in date order.</summary>
    /// <param name="tokens">Tokens of the plan</param>
    /// <param name="context">Context the plan was produced from</param>
    internal static List<(Guid ShiftRefId, DateOnly Date)> MissingAssignments(
        IReadOnlyList<CoreToken> tokens, CoreWizardContext context)
    {
        var evaluation = EvaluationContext.For(context);
        var assigned = new Dictionary<(Guid, DateOnly), int>();
        foreach (var token in tokens)
        {
            var key = (token.ShiftRefId, token.Date);
            assigned[key] = assigned.GetValueOrDefault(key) + 1;
        }

        var missing = new List<(Guid ShiftRefId, DateOnly Date)>();
        foreach (var entry in evaluation.SlotsByKey.OrderBy(e => e.Key.Date).ThenBy(e => e.Key.ShiftRefId))
        {
            var capacity = evaluation.SlotCapacities.GetValueOrDefault(
                entry.Key, Math.Max(1, entry.Value.RequiredAssignments));
            for (var held = assigned.GetValueOrDefault(entry.Key); held < capacity; held++)
            {
                missing.Add(entry.Key);
            }
        }

        return missing;
    }

    /// <summary>A shift as the report names it: day, shift name and times.</summary>
    /// <param name="context">Context holding the shift definitions</param>
    /// <param name="shiftRefId">Shift definition</param>
    /// <param name="date">Day of the shift</param>
    internal static string Label(CoreWizardContext context, Guid shiftRefId, DateOnly date)
    {
        var name = EvaluationContext.For(context).SlotsByKey.TryGetValue((shiftRefId, date), out var slot)
            ? $"{slot.Name} {slot.StartTime}-{slot.EndTime}"
            : shiftRefId.ToString();
        return date.ToString(DayFormat, CultureInfo.InvariantCulture) + " " + name;
    }

    private static int PackageRestExceptions(
        SwapChainProbeAdmission strictRest, SwapChainProbeVariant variant, List<CoreToken> before, Found found)
    {
        var count = 0;
        for (var index = 0; index < found.Agents.Count; index++)
        {
            var agent = strictRest.AgentOf(found.Agents[index]);
            if (agent is null)
            {
                continue;
            }

            var rung = index == 0 ? variant.OpenSlot : variant.Receiver;
            var refusedBefore = strictRest.Inadmissible(agent, before, rung);
            count += strictRest.Inadmissible(agent, found.Tokens, rung).Count(token => !refusedBefore.Contains(token));
        }

        return count;
    }

    private sealed record Found(List<CoreToken> Tokens, int Moves, List<string> Steps, List<string> Agents);

    private sealed class Search
    {
        private readonly CoreWizardContext _context;
        private readonly SwapChainProbeVariant _variant;
        private readonly EvaluationContext _evaluation;
        private readonly SwapChainProbeAdmission _admission;
        private readonly long _checksAtStart;

        public Search(CoreWizardContext context, SwapChainProbeVariant variant, SwapChainProbeAdmission admission)
        {
            _context = context;
            _variant = variant;
            _evaluation = EvaluationContext.For(context);
            _admission = admission;
            _checksAtStart = admission.Checks;
        }

        public long Probes => _admission.Checks - _checksAtStart;

        public bool Exhausted => Probes > ProbeBudgetPerSlot;

        public Found? Close((Guid ShiftRefId, DateOnly Date) key, List<CoreToken> tokens)
        {
            if (!_evaluation.SlotsByKey.TryGetValue(key, out var slot))
            {
                return null;
            }

            var item = SwapChainProbeItem.ForOpenSlot(key.ShiftRefId, key.Date, slot);
            for (var moves = 1; moves <= MaxMoves && !Exhausted; moves++)
            {
                var steps = new List<string>();
                var agents = new List<string>();
                var result = Find(item, tokens, new HashSet<string>(StringComparer.Ordinal), moves, steps, agents);
                if (result is not null)
                {
                    return new Found(result, moves, steps, agents);
                }
            }

            return null;
        }

        private List<CoreToken>? Find(
            SwapChainProbeItem item,
            List<CoreToken> tokens,
            HashSet<string> used,
            int moves,
            List<string> steps,
            List<string> agents)
        {
            var rung = item.Source is null ? _variant.OpenSlot : _variant.Receiver;
            var holders = tokens
                .Where(t => t.ShiftRefId == item.ShiftRefId && t.Date == item.Date)
                .Select(t => t.AgentId)
                .ToHashSet(StringComparer.Ordinal);
            var candidates = _context.Agents
                .Where(a => !a.IsStandIn && !used.Contains(a.Id) && !holders.Contains(a.Id))
                .ToList();

            return moves == 1
                ? PlaceDirectly(item, tokens, candidates, rung, steps, agents)
                : PlaceAfterHandingOn(item, tokens, candidates, used, moves, rung, steps, agents);
        }

        private List<CoreToken>? PlaceDirectly(
            SwapChainProbeItem item,
            List<CoreToken> tokens,
            List<CoreAgent> candidates,
            SlotRelaxation rung,
            List<string> steps,
            List<string> agents)
        {
            foreach (var agent in candidates)
            {
                if (!Admits(agent, item, tokens, rung))
                {
                    continue;
                }

                var placed = new List<CoreToken>(tokens) { _admission.TokenFor(item, agent) };
                if (_admission.FirstNewInadmissible(_admission.Inadmissible(agent, tokens, rung), agent, placed, rung) is not null)
                {
                    continue;
                }

                steps.Add(Describe(item, agent, null));
                agents.Add(agent.Id);
                return placed;
            }

            return null;
        }

        private List<CoreToken>? PlaceAfterHandingOn(
            SwapChainProbeItem item,
            List<CoreToken> tokens,
            List<CoreAgent> candidates,
            HashSet<string> used,
            int moves,
            SlotRelaxation rung,
            List<string> steps,
            List<string> agents)
        {
            foreach (var agent in candidates)
            {
                var own = tokens
                    .Where(t => !t.IsLocked && string.Equals(t.AgentId, agent.Id, StringComparison.Ordinal))
                    .OrderBy(t => Math.Abs(t.Date.DayNumber - item.Date.DayNumber))
                    .ThenBy(t => t.StartAt)
                    .ToList();
                if (own.Count == 0)
                {
                    continue;
                }

                HashSet<CoreToken>? inadmissibleBefore = null;
                foreach (var relief in own)
                {
                    if (Exhausted)
                    {
                        return null;
                    }

                    var working = SwapChainProbeAdmission.Without(tokens, relief);
                    if (!Admits(agent, item, working, rung))
                    {
                        continue;
                    }

                    working.Add(_admission.TokenFor(item, agent));
                    inadmissibleBefore ??= _admission.Inadmissible(agent, tokens, rung);
                    if (_admission.FirstNewInadmissible(inadmissibleBefore, agent, working, rung) is not null)
                    {
                        continue;
                    }

                    used.Add(agent.Id);
                    var tail = new List<string>();
                    var tailAgents = new List<string>();
                    var result = Find(SwapChainProbeItem.ForHandedOn(relief), working, used, moves - 1, tail, tailAgents);
                    used.Remove(agent.Id);
                    if (result is null)
                    {
                        continue;
                    }

                    steps.Add(Describe(item, agent, relief));
                    steps.AddRange(tail);
                    agents.Add(agent.Id);
                    agents.AddRange(tailAgents);
                    return result;
                }
            }

            return null;
        }

        private bool Admits(CoreAgent agent, SwapChainProbeItem item, List<CoreToken> tokens, SlotRelaxation rung)
            => !Exhausted && _admission.Admits(agent, item, tokens, rung);

        private string Describe(SwapChainProbeItem item, CoreAgent agent, CoreToken? relief)
        {
            var text = $"{agent.Id} takes {Label(_context, item.ShiftRefId, item.Date)}";
            return relief is null ? text : text + $" and hands on {Label(_context, relief.ShiftRefId, relief.Date)}";
        }
    }
}
