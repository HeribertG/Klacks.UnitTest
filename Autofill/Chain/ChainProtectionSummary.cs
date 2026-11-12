// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;

namespace Klacks.UnitTest.Autofill.Chain;

/// <summary>
/// The lines of the protection comparison the stage table cannot show: how each Wizard 3 run ended and what kind of
/// moves it made against the plan it started from (the Wizard 1 plan for production, the Wizard 2 plan for the former chain). A work that went to a person who already worked that day is an exchange
/// of shift kinds; a work that went to a person who was free that day moved hours between two people. The count of
/// person-days worked now but free in the start plan is the stricter reading of the same thing and stays 0 when
/// only shift kinds are exchanged. Limit: the protection is judged batch by batch against the plan right before the
/// batch, not against the start plan.
/// </summary>
public static class ChainProtectionSummary
{
    private const string FitnessFormat = "0.0000";

    /// <param name="runs">The production run and the former chain's run, each with the plan it started from</param>
    public static string Render(IReadOnlyList<ChainProtectedRun> runs)
    {
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(
            "Wizard 3 runs of the comparison: production starts from the Wizard 1 plan, the former chain from the Wizard 2 plan:");
        foreach (var run in runs)
        {
            var (exchanged, toFree) = MovesAgainst(run.Start, run.Works);
            text.AppendLine(
                run.Label + " (" + run.Description + "): stop " + run.Search.StopReason + ", harmony fitness "
                + run.Search.FitnessBefore.ToString(FitnessFormat, CultureInfo.InvariantCulture) + " -> "
                + run.Search.FitnessAfter.ToString(FitnessFormat, CultureInfo.InvariantCulture) + ", applied batches "
                + Number(run.Search.AppliedBatches.Count) + ", evaluations "
                + run.Search.Evaluations.ToString(CultureInfo.InvariantCulture) + "; works moved against " + run.StartLabel + " "
                + Number(exchanged + toFree) + " (shift kind exchanged " + Number(exchanged)
                + ", moved to a free person " + Number(toFree) + "); person-days worked now but free in " + run.StartLabel + " "
                + Number(NewlyWorkedDays(run.Start, run.Works)));
        }

        return text.ToString();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int NewlyWorkedDays(IReadOnlyList<ChainWork> start, IReadOnlyList<ChainWork> after)
    {
        var workedBefore = start.Select(w => (w.Token.AgentId, w.Token.Date)).ToHashSet();
        return after.Select(w => (w.Token.AgentId, w.Token.Date)).Distinct().Count(day => !workedBefore.Contains(day));
    }

    private static (int Exchanged, int ToFree) MovesAgainst(IReadOnlyList<ChainWork> start, IReadOnlyList<ChainWork> after)
    {
        var holderBefore = start.ToDictionary(w => w.WorkId, w => w.Token.AgentId);
        var workedBefore = start.Select(w => (w.Token.AgentId, w.Token.Date)).ToHashSet();
        var exchanged = 0;
        var toFree = 0;
        foreach (var work in after)
        {
            if (!holderBefore.TryGetValue(work.WorkId, out var before)
                || string.Equals(before, work.Token.AgentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (workedBefore.Contains((work.Token.AgentId, work.Token.Date)))
            {
                exchanged++;
            }
            else
            {
                toFree++;
            }
        }

        return (exchanged, toFree);
    }
}
