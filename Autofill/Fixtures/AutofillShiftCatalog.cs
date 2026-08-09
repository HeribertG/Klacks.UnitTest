// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The three daily shifts of the autofill suite: their stable ids, their times and the translation
/// between a shift kind and the engine's shift-type index.
/// <para>
/// The shift kind early/late/night is nowhere persisted; the engine derives it from the time span in
/// <see cref="ShiftTypeInference"/>, whose banding rule was last changed on 2026-08-06. Every fixture
/// therefore has to prove, not assume, that 07:00-15:00 / 15:00-23:00 / 23:00-07:00 still land on
/// early / late / night — that is what <see cref="ValidateShiftTypeInference"/> is for. The builder
/// calls it on every Build(), and a scenario test can call it directly as an explicit guard.
/// </para>
/// </summary>
public static class AutofillShiftCatalog
{
    /// <summary>Ids are real Guids: the Stage-0 qualification, blacklist and window checks parse the
    /// shift id and fail open on anything unparseable, which would silently disable them.</summary>
    public static readonly Guid EarlyShiftId = new("00000000-0000-0000-0000-0000000a0f01");

    public static readonly Guid LateShiftId = new("00000000-0000-0000-0000-0000000a0f02");

    public static readonly Guid NightShiftId = new("00000000-0000-0000-0000-0000000a0f03");

    /// <summary>
    /// Order index of the three order-less shift ids above: the single unnamed order every scenario
    /// before scenario 4 plans. It is deliberately not 1 — an order index of 1 addresses the first of
    /// the multi-order id triples, which is a different Guid.
    /// </summary>
    public const int SingleOrderIndex = 0;

    /// <summary>Lowest real order index; order indices run from here to <see cref="MaxOrderCount"/>.</summary>
    public const int FirstOrderIndex = 1;

    /// <summary>Highest order index the pinned id table covers.</summary>
    public const int MaxOrderCount = 3;

    /// <summary>Answer of <see cref="OrderOf"/> for a shift id that is not in this catalog.</summary>
    public const int UnknownOrderIndex = -1;

    /// <summary>Matrix symbol of an unused day.</summary>
    public const char FreeSymbol = '.';

    /// <summary>
    /// Matrix symbol of a day carrying more than one shift for the same employee. It marks the day,
    /// it does not judge it: two different shifts that do not overlap are allowed since the owner
    /// correction of 2026-08-08. Conflicts are listed in the metrics JSON, not in the matrix.
    /// </summary>
    public const char MultipleShiftsSymbol = 'X';

    private const char EarlySymbol = 'F';
    private const char LateSymbol = 'S';
    private const char NightSymbol = 'N';

    private const string OrderNamePrefix = "B";

    private const string OrderNameSeparator = " ";

    private static readonly AutofillShiftKind[] AllKinds =
        [AutofillShiftKind.Early, AutofillShiftKind.Late, AutofillShiftKind.Night];

    /// <summary>
    /// The nine pinned ids of the multi-order scenarios, addressed as [order index][kind index]. The
    /// literals are chosen so that their ordinal string order — the only discriminator the auction has
    /// when three orders are cut identically, <c>SlotAuctioneer</c> sorting date, start time and then
    /// id ordinally — runs order 1 before order 2 before order 3 inside every shift kind.
    /// <see cref="ValidateOrderIdOrdering"/> proves that instead of assuming it.
    /// </summary>
    private static readonly Guid[][] MultiOrderShiftIds =
    [
        [
            new("00000000-0000-0000-0000-0000000a0f11"),
            new("00000000-0000-0000-0000-0000000a0f12"),
            new("00000000-0000-0000-0000-0000000a0f13"),
        ],
        [
            new("00000000-0000-0000-0000-0000000a0f21"),
            new("00000000-0000-0000-0000-0000000a0f22"),
            new("00000000-0000-0000-0000-0000000a0f23"),
        ],
        [
            new("00000000-0000-0000-0000-0000000a0f31"),
            new("00000000-0000-0000-0000-0000000a0f32"),
            new("00000000-0000-0000-0000-0000000a0f33"),
        ],
    ];

    private static readonly Dictionary<Guid, int> OrderByShiftId = BuildOrderByShiftId();

    /// <summary>All three kinds in the order early, late, night — the rotation order of rule 5.</summary>
    public static IReadOnlyList<AutofillShiftKind> Kinds => AllKinds;

    /// <param name="kind">Shift kind</param>
    public static Guid ShiftIdOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => EarlyShiftId,
        AutofillShiftKind.Late => LateShiftId,
        AutofillShiftKind.Night => NightShiftId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <summary>
    /// Id of one shift of one order. <see cref="SingleOrderIndex"/> returns the order-less id of the
    /// scenarios that plan a single order, so a fixture that never names an order keeps addressing
    /// exactly the shifts it addressed before the order dimension existed.
    /// </summary>
    /// <param name="orderIndex">Order the shift belongs to, or <see cref="SingleOrderIndex"/></param>
    /// <param name="kind">Shift kind</param>
    public static Guid ShiftIdOf(int orderIndex, AutofillShiftKind kind)
    {
        if (orderIndex == SingleOrderIndex)
        {
            return ShiftIdOf(kind);
        }

        EnsureKnownOrder(orderIndex);
        return MultiOrderShiftIds[orderIndex - FirstOrderIndex][ShiftTypeIndexOf(kind)];
    }

    /// <summary>
    /// Order an id belongs to: <see cref="SingleOrderIndex"/> for the order-less triple, the order
    /// index for one of the nine multi-order ids and <see cref="UnknownOrderIndex"/> for anything the
    /// catalog does not know. It never throws, because the analyzer resolves the order of every token
    /// of every scenario and an unknown id has to stay reportable instead of ending the measurement.
    /// </summary>
    /// <param name="shiftId">Shift reference id of a token, locked work or demanded slot</param>
    public static int OrderOf(Guid shiftId)
        => OrderByShiftId.TryGetValue(shiftId, out var order) ? order : UnknownOrderIndex;

    /// <param name="orderIndex">Order the shift belongs to, or <see cref="SingleOrderIndex"/></param>
    /// <param name="kind">Shift kind</param>
    public static string NameOf(int orderIndex, AutofillShiftKind kind)
        => orderIndex == SingleOrderIndex
            ? NameOf(kind)
            : OrderNamePrefix + orderIndex.ToString(CultureInfo.InvariantCulture) + OrderNameSeparator + NameOf(kind);

    /// <summary>
    /// Two-character matrix symbol of a multi-order plan: the order digit followed by the kind letter,
    /// for example <c>1F</c>. The single-character symbol stays what it was, so a plan with one order
    /// draws exactly the matrix it drew before.
    /// </summary>
    /// <param name="orderIndex">Order the shift belongs to</param>
    /// <param name="kind">Shift kind</param>
    public static string OrderSymbolOf(int orderIndex, AutofillShiftKind kind)
        => orderIndex.ToString(CultureInfo.InvariantCulture) + SymbolOf(kind);

    /// <param name="kind">Shift kind</param>
    public static string NameOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => AutofillSpecConstants.EarlyShiftName,
        AutofillShiftKind.Late => AutofillSpecConstants.LateShiftName,
        AutofillShiftKind.Night => AutofillSpecConstants.NightShiftName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <param name="kind">Shift kind</param>
    public static string StartTimeOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => AutofillSpecConstants.EarlyStartTime,
        AutofillShiftKind.Late => AutofillSpecConstants.LateStartTime,
        AutofillShiftKind.Night => AutofillSpecConstants.NightStartTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <param name="kind">Shift kind</param>
    public static string EndTimeOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => AutofillSpecConstants.EarlyEndTime,
        AutofillShiftKind.Late => AutofillSpecConstants.LateEndTime,
        AutofillShiftKind.Night => AutofillSpecConstants.NightEndTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <summary>Single character used by the plan matrix.</summary>
    /// <param name="kind">Shift kind</param>
    public static char SymbolOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => EarlySymbol,
        AutofillShiftKind.Late => LateSymbol,
        AutofillShiftKind.Night => NightSymbol,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <summary>Turns an engine shift-type index back into a kind.</summary>
    /// <param name="shiftTypeIndex">0 = early, 1 = late, 2 = night</param>
    public static AutofillShiftKind FromShiftTypeIndex(int shiftTypeIndex) => shiftTypeIndex switch
    {
        ShiftTypeInference.EarlyIndex => AutofillShiftKind.Early,
        ShiftTypeInference.LateIndex => AutofillShiftKind.Late,
        ShiftTypeInference.NightIndex => AutofillShiftKind.Night,
        _ => throw new ArgumentOutOfRangeException(
            nameof(shiftTypeIndex), shiftTypeIndex, "Shift type index outside the early/late/night range."),
    };

    /// <summary>The engine index a kind is expected to map to.</summary>
    /// <param name="kind">Shift kind</param>
    public static int ShiftTypeIndexOf(AutofillShiftKind kind) => (int)kind;

    /// <summary>
    /// Absolute start and end of one shift instance. A night shift starts on <paramref name="date"/>
    /// at 23:00 and ends on the following calendar day, so its span crosses midnight while its date
    /// stays the start day — the definition the whole suite uses for "night shift date".
    /// </summary>
    /// <param name="kind">Shift kind</param>
    /// <param name="date">Calendar day the shift starts on</param>
    public static (DateTime StartAt, DateTime EndAt) SpanOf(AutofillShiftKind kind, DateOnly date)
    {
        var start = date.ToDateTime(TimeOnly.Parse(StartTimeOf(kind)));
        var end = start.AddHours(AutofillSpecConstants.ShiftHours);
        return (start, end);
    }

    /// <summary>Builds the three daily shifts for every day of the given range, one slot each.</summary>
    /// <param name="from">First day, inclusive</param>
    /// <param name="until">Last day, inclusive</param>
    public static IReadOnlyList<CoreShift> BuildDailyShifts(DateOnly from, DateOnly until)
    {
        var shifts = new List<CoreShift>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            var iso = date.ToString(AutofillSpecConstants.IsoDateFormat);
            foreach (var kind in AllKinds)
            {
                shifts.Add(new CoreShift(
                    Id: ShiftIdOf(kind).ToString(),
                    Name: NameOf(kind),
                    Date: iso,
                    StartTime: StartTimeOf(kind),
                    EndTime: EndTimeOf(kind),
                    Hours: AutofillSpecConstants.ShiftHours,
                    RequiredAssignments: 1,
                    Priority: 0));
            }
        }

        return shifts;
    }

    /// <summary>
    /// Builds the daily shifts of <paramref name="orderCount"/> orders that are cut identically: every
    /// day carries three shifts per order, each a slot of its own with one required assignment. Three
    /// orders are deliberately NOT modelled as one shift of quantity three — the evaluation context
    /// keys its slots on (shift id, date) and would collapse them into a single capacity-three slot,
    /// which erases the order dimension the whole scenario measures.
    /// </summary>
    /// <param name="from">First day, inclusive</param>
    /// <param name="until">Last day, inclusive</param>
    /// <param name="orderCount">Number of parallel orders, 1 to <see cref="MaxOrderCount"/></param>
    public static IReadOnlyList<CoreShift> BuildDailyShifts(DateOnly from, DateOnly until, int orderCount)
    {
        EnsureKnownOrder(orderCount);

        var shifts = new List<CoreShift>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            var iso = date.ToString(AutofillSpecConstants.IsoDateFormat);
            for (var order = FirstOrderIndex; order < FirstOrderIndex + orderCount; order++)
            {
                foreach (var kind in AllKinds)
                {
                    shifts.Add(new CoreShift(
                        Id: ShiftIdOf(order, kind).ToString(),
                        Name: NameOf(order, kind),
                        Date: iso,
                        StartTime: StartTimeOf(kind),
                        EndTime: EndTimeOf(kind),
                        Hours: AutofillSpecConstants.ShiftHours,
                        RequiredAssignments: 1,
                        Priority: 0));
                }
            }
        }

        return shifts;
    }

    /// <summary>
    /// Proves the property the whole order dimension rests on: with identical dates and start times
    /// the shift id is the auction's only discriminator, so the nine pinned ids must sort order 1
    /// before order 2 before order 3 inside every kind under an ordinal string comparison. An empty
    /// result means the pinned literals still produce that order.
    /// </summary>
    public static IReadOnlyList<string> ValidateOrderIdOrdering()
    {
        var problems = new List<string>();
        foreach (var kind in AllKinds)
        {
            for (var order = FirstOrderIndex; order < MaxOrderCount; order++)
            {
                var lower = ShiftIdOf(order, kind).ToString();
                var higher = ShiftIdOf(order + 1, kind).ToString();
                if (string.CompareOrdinal(lower, higher) >= 0)
                {
                    problems.Add(
                        $"Shift kind {kind}: the id of order {order.ToString(CultureInfo.InvariantCulture)} ({lower}) "
                        + $"must sort before the id of order {(order + 1).ToString(CultureInfo.InvariantCulture)} "
                        + $"({higher}) under an ordinal string comparison, because that comparison is the auction's "
                        + "only discriminator between identically cut orders.");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The order the auction awards the shifts of one day in, written out as text: date and start time
    /// are equal across orders, so the ordinal id order decides. Used by the scenario tests to record
    /// the processing order in the artifacts instead of leaving it implicit.
    /// </summary>
    /// <param name="orderCount">Number of parallel orders</param>
    public static string DescribeAuctionOrderOfOneDay(int orderCount)
    {
        EnsureKnownOrder(orderCount);

        var slots = new List<(string Id, string StartTime, string Label)>();
        for (var order = FirstOrderIndex; order < FirstOrderIndex + orderCount; order++)
        {
            foreach (var kind in AllKinds)
            {
                slots.Add((
                    ShiftIdOf(order, kind).ToString(),
                    StartTimeOf(kind),
                    OrderSymbolOf(order, kind)));
            }
        }

        return string.Join(
            " < ",
            slots
                .OrderBy(s => s.StartTime, StringComparer.Ordinal)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => s.Label));
    }

    /// <summary>
    /// Runs the production inference over the suite's three time spans and reports every kind whose
    /// span no longer classifies as intended. An empty result means the fixture times still mean what
    /// the specification says they mean.
    /// </summary>
    public static IReadOnlyList<string> ValidateShiftTypeInference()
    {
        var problems = new List<string>();
        foreach (var kind in AllKinds)
        {
            var expected = ShiftTypeIndexOf(kind);
            var actual = ShiftTypeInference.FromSpanString(StartTimeOf(kind), EndTimeOf(kind));
            if (actual != expected)
            {
                problems.Add(
                    $"Shift kind {kind} ({StartTimeOf(kind)}-{EndTimeOf(kind)}) is classified as shift type index "
                    + $"{actual} but the specification requires {expected}. ShiftTypeInference changed - the "
                    + "fixture times no longer produce an early/late/night triple.");
            }
        }

        return problems;
    }

    private static void EnsureKnownOrder(int orderIndex)
    {
        if (orderIndex < FirstOrderIndex || orderIndex > MaxOrderCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderIndex),
                orderIndex,
                $"The catalog pins ids for orders {FirstOrderIndex.ToString(CultureInfo.InvariantCulture)} to "
                + $"{MaxOrderCount.ToString(CultureInfo.InvariantCulture)} only.");
        }
    }

    private static Dictionary<Guid, int> BuildOrderByShiftId()
    {
        var byId = new Dictionary<Guid, int>
        {
            [EarlyShiftId] = SingleOrderIndex,
            [LateShiftId] = SingleOrderIndex,
            [NightShiftId] = SingleOrderIndex,
        };

        for (var order = FirstOrderIndex; order <= MaxOrderCount; order++)
        {
            foreach (var id in MultiOrderShiftIds[order - FirstOrderIndex])
            {
                byId[id] = order;
            }
        }

        return byId;
    }
}
