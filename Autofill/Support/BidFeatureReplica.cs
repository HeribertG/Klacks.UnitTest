// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Common.Fuzzy;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Line-by-line replica of <c>FuzzyBiddingAgent.ExtractFeatures</c>, which is private and therefore
/// unreachable from a test. The replica is only trustworthy while it agrees with the engine, so
/// every diagnosis that uses it must first feed the replica's features through the same inference
/// engine and compare the crisp output with the recorded bid score. A drift then shows up as a red
/// test instead of as a wrong table.
/// </summary>
public static class BidFeatureReplica
{
    /// <summary>Block hunger in hours; the value the fuzzy variable of the same name reads.</summary>
    public const string BlockHunger = "BlockHunger";

    /// <summary>Days the in-progress block already runs.</summary>
    public const string BlockMaturity = "BlockMaturity";

    /// <summary>Days since the agent last worked the shift kind of the slot.</summary>
    public const string SlotDaysSince = "SlotDaysSince";

    /// <summary>Accumulated hours of the run divided by the weekly cap.</summary>
    public const string WeeklyLoad = "WeeklyLoad";

    /// <summary>Roster position normalised to zero for the top of the list.</summary>
    public const string IndexBonus = "IndexBonus";

    /// <summary>One when a new block would repeat the previous block's kind.</summary>
    public const string NewBlockSameType = "NewBlockSameType";

    private const string DaysSinceEarly = "DaysSinceEarly";

    private const string DaysSinceLate = "DaysSinceLate";

    private const string DaysSinceNight = "DaysSinceNight";

    private const double BlockSaturationHours = 36.0;

    private const double HoursPerBlockDay = 8.0;

    private const double FallbackWeeklyCap = 50.0;

    private const double NeverWorkedDays = 30.0;

    private const int EarlyIndex = 0;

    private const int LateIndex = 1;

    private const int NightIndex = 2;

    private const string TermFormat = "0.00";

    private const string ValueFormat = "0.###";

    private const string TermSeparator = "/";

    private const string NoTerm = "-";

    private static readonly IReadOnlyDictionary<string, LinguisticVariable> Variables =
        DefaultLinguisticVariables.BuildInputs();

    /// <summary>Builds the crisp feature vector the bidding agent would build for this call.</summary>
    /// <param name="agent">Bidding agent's contract data</param>
    /// <param name="slot">Slot being auctioned</param>
    /// <param name="state">Runtime state at the moment of the bid</param>
    /// <param name="context">Engine input; its agent order defines the roster position</param>
    public static Dictionary<string, double> Extract(
        CoreAgent agent, CoreShift slot, AgentRuntimeState state, CoreWizardContext context)
    {
        var startsNewBlock = StartsNewBlock(slot, state);
        var effectiveBlockLength = startsNewBlock ? 0 : state.CurrentBlockLength;
        var consumedThisBlock = effectiveBlockLength * HoursPerBlockDay;
        var blockHunger = Math.Max(0, BlockSaturationHours - consumedThisBlock);
        var weeklyCap = agent.MaxWeeklyHours > 0 ? agent.MaxWeeklyHours : FallbackWeeklyCap;
        var weeklyLoad = (agent.CurrentHours + state.HoursAssignedThisRun) / weeklyCap;
        var slotTypeIndex = ShiftTypeInference.FromSpanString(slot.StartTime, slot.EndTime);

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [BlockHunger] = blockHunger,
            [BlockMaturity] = effectiveBlockLength,
            [DaysSinceEarly] = ToDouble(state.DaysSinceShiftType, EarlyIndex),
            [DaysSinceLate] = ToDouble(state.DaysSinceShiftType, LateIndex),
            [DaysSinceNight] = ToDouble(state.DaysSinceShiftType, NightIndex),
            [SlotDaysSince] = ToDouble(state.DaysSinceShiftType, slotTypeIndex),
            [WeeklyLoad] = weeklyLoad,
            [IndexBonus] = ResolveIndexBonus(agent, context),
            [NewBlockSameType] = ResolveNewBlockSameType(agent, state, slotTypeIndex, startsNewBlock),
        };
    }

    /// <summary>True when the slot would open a new work block instead of continuing the current one.</summary>
    /// <param name="slot">Slot being auctioned</param>
    /// <param name="state">Runtime state at the moment of the bid</param>
    public static bool StartsNewBlock(CoreShift slot, AgentRuntimeState state)
    {
        if (!state.LastWorkedDate.HasValue)
        {
            return true;
        }

        return DateOnly.TryParse(slot.Date, out var slotDate)
            && slotDate.DayNumber - state.LastWorkedDate.Value.DayNumber > 1;
    }

    /// <summary>
    /// Renders a crisp value together with every term it belongs to, so a table row shows the fuzzy
    /// reading and not only the number the reader would have to fuzzify in their head.
    /// </summary>
    /// <param name="variable">Name of the linguistic variable</param>
    /// <param name="crisp">Crisp value of that variable</param>
    public static string Describe(string variable, double crisp)
    {
        var value = crisp.ToString(ValueFormat, CultureInfo.InvariantCulture);
        if (!Variables.TryGetValue(variable, out var linguistic))
        {
            return value;
        }

        var terms = linguistic.TermNames
            .Select(term => (Term: term, Mu: linguistic.Mu(term, crisp)))
            .Where(candidate => candidate.Mu > 0)
            .OrderByDescending(candidate => candidate.Mu)
            .Select(candidate =>
                candidate.Term + " " + candidate.Mu.ToString(TermFormat, CultureInfo.InvariantCulture))
            .ToList();

        return value + " [" + (terms.Count == 0 ? NoTerm : string.Join(TermSeparator, terms)) + "]";
    }

    private static double ResolveNewBlockSameType(
        CoreAgent agent, AgentRuntimeState state, int slotTypeIndex, bool startsNewBlock)
    {
        if (!agent.PerformsShiftWork || state.CurrentBlockStartShiftType < 0)
        {
            return 0.0;
        }

        return startsNewBlock && state.CurrentBlockStartShiftType == slotTypeIndex ? 1.0 : 0.0;
    }

    private static double ResolveIndexBonus(CoreAgent agent, CoreWizardContext context)
    {
        for (var i = 0; i < context.Agents.Count; i++)
        {
            if (context.Agents[i].Id == agent.Id)
            {
                return context.Agents.Count <= 1 ? 0.0 : (double)i / (context.Agents.Count - 1);
            }
        }

        return 1.0;
    }

    private static double ToDouble(IReadOnlyList<int> values, int index)
    {
        if (index < 0 || index >= values.Count)
        {
            return NeverWorkedDays;
        }

        var value = values[index];
        return value == int.MaxValue ? NeverWorkedDays : value;
    }
}
