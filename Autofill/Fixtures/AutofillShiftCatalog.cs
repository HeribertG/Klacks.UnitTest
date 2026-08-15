// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;

namespace Klacks.UnitTest.Autofill.Fixtures;

/// <summary>
/// The daily shifts of the autofill suite: their stable ids, their times and the translation between
/// a shift kind and the engine's shift-type index.
/// <para>
/// The shift kind early/late/night is nowhere persisted; the engine derives it from the time span in
/// <see cref="ShiftTypeInference"/>, whose banding rule was last changed on 2026-08-06. Every fixture
/// therefore has to prove, not assume, that 07:00-15:00 / 15:00-23:00 / 23:00-07:00 still land on
/// early / late / night — that is what <see cref="ValidateShiftTypeInference"/> is for. The builder
/// calls it on every Build(), and a scenario test can call it directly as an explicit guard.
/// </para>
/// <para>
/// SLOT KIND versus SHIFT CLASS. Since scenario 5 the two are not the same thing. A slot kind is what
/// a fixture demands — early, late, night or the day shift 08:00-16:00 — and it addresses one pinned
/// id through <see cref="SlotIndexOf"/>. A shift class is what the engine infers from the time span,
/// and there are only three of them: the day shift's span classifies as LATE, so
/// <see cref="ShiftTypeIndexOf"/> maps day and late onto the same index and is deliberately not
/// injective. Everything about rotation, purity and fairness reasons in classes and uses
/// <see cref="Kinds"/>; everything that builds or identifies a slot reasons in slot kinds and uses
/// <see cref="SlotKinds"/> or <see cref="SlotKindOf"/>. Confusing the two is the one way to make a
/// day shift silently disappear into the late statistics or a late shift silently count as a day one.
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
    /// <para>
    /// It was 'X' until scenario 6, which the specification of 2026-08-14 requires to draw an absence
    /// day as 'X'. Two different states may not share one glyph in a matrix a human reads, and the
    /// specification names the absence marker, so the multiple-shifts marker moved.
    /// </para>
    /// </summary>
    public const char MultipleShiftsSymbol = '*';

    /// <summary>
    /// Matrix symbol of a booked absence day — scenario 6. It is deliberately NOT the free-day dot: an
    /// absence day is blocked and paid, a free day is neither, and the whole point of the scenario is
    /// that the two are not the same thing.
    /// </summary>
    public const char AbsenceSymbol = 'X';

    /// <summary>
    /// Matrix symbol of a day a FREE schedule command closes. Also not the free-day dot: the day is
    /// blocked by an input rather than left empty by the plan, and unlike an absence it pays nothing.
    /// </summary>
    public const char FreeCommandSymbol = 'f';

    private const char EarlySymbol = 'F';
    private const char LateSymbol = 'S';
    private const char NightSymbol = 'N';

    /// <summary>Matrix symbol of a day shift; scenario 5 asks for it explicitly.</summary>
    private const char DaySymbol = 'T';

    private const string OrderNamePrefix = "B";

    private const string OrderNameSeparator = " ";

    private static readonly AutofillShiftKind[] AllKinds =
        [AutofillShiftKind.Early, AutofillShiftKind.Late, AutofillShiftKind.Night];

    /// <summary>
    /// Every slot kind a fixture can demand, the day shift included. Kept apart from
    /// <see cref="AllKinds"/> on purpose: that list is the three ROTATION CLASSES of rule 5, and the
    /// day shift is not a fourth class — it is a fourth slot whose class is late.
    /// </summary>
    private static readonly AutofillShiftKind[] AllSlotKinds =
        [AutofillShiftKind.Early, AutofillShiftKind.Late, AutofillShiftKind.Night, AutofillShiftKind.Day];

    /// <summary>
    /// The twelve pinned ids of the multi-order scenarios, addressed as [order index][slot index]. The
    /// literals are chosen so that their ordinal string order — the only discriminator the auction has
    /// when the orders are cut identically, <c>SlotAuctioneer</c> sorting date, start time and then
    /// id ordinally — runs order 1 before order 2 before order 3 inside every slot kind.
    /// <see cref="ValidateOrderIdOrdering"/> proves that instead of assuming it. The fourth entry of
    /// every row is the day shift of scenario 5; a scenario that never asks for it never builds a slot
    /// with that id, so the three-shift scenarios address exactly the ids they addressed before.
    /// </summary>
    private static readonly Guid[][] MultiOrderShiftIds =
    [
        [
            new("00000000-0000-0000-0000-0000000a0f11"),
            new("00000000-0000-0000-0000-0000000a0f12"),
            new("00000000-0000-0000-0000-0000000a0f13"),
            new("00000000-0000-0000-0000-0000000a0f14"),
        ],
        [
            new("00000000-0000-0000-0000-0000000a0f21"),
            new("00000000-0000-0000-0000-0000000a0f22"),
            new("00000000-0000-0000-0000-0000000a0f23"),
            new("00000000-0000-0000-0000-0000000a0f24"),
        ],
        [
            new("00000000-0000-0000-0000-0000000a0f31"),
            new("00000000-0000-0000-0000-0000000a0f32"),
            new("00000000-0000-0000-0000-0000000a0f33"),
            new("00000000-0000-0000-0000-0000000a0f34"),
        ],
    ];

    /// <summary>
    /// Identity of an order, the value the engine reads as <c>LocationContext</c>. Production derives
    /// it from the shift hierarchy (<c>RootId ?? OriginalId ?? Id</c>), so the fixture uses Guid
    /// strings too instead of a prettier label. Index 0 is the single unnamed order of the scenarios
    /// before scenario 4: those plans stay on ONE order, which is exactly why their continuity score
    /// must remain the constant 1 the engine produced while the field was null everywhere.
    /// </summary>
    private static readonly Guid[] OrderIdentities =
    [
        new("00000000-0000-0000-0000-0000000a0b00"),
        new("00000000-0000-0000-0000-0000000a0b01"),
        new("00000000-0000-0000-0000-0000000a0b02"),
        new("00000000-0000-0000-0000-0000000a0b03"),
    ];

    private static readonly Dictionary<Guid, int> OrderByShiftId = BuildOrderByShiftId();

    private static readonly Dictionary<Guid, AutofillShiftKind> SlotKindByShiftId = BuildSlotKindByShiftId();

    /// <summary>All three kinds in the order early, late, night — the rotation order of rule 5.</summary>
    public static IReadOnlyList<AutofillShiftKind> Kinds => AllKinds;

    /// <summary>
    /// Every slot kind including the day shift, in slot order. Use this to BUILD slots; use
    /// <see cref="Kinds"/> to reason about rotation, fairness and purity, which know three classes.
    /// </summary>
    public static IReadOnlyList<AutofillShiftKind> SlotKinds => AllSlotKinds;

    /// <summary>
    /// Slot kinds of a scenario: the three classic ones, plus the day shift when the scenario demands
    /// it. The list order is the order the shifts are appended in.
    /// </summary>
    /// <param name="includeDayShift">True when the scenario cuts a day shift per order</param>
    public static IReadOnlyList<AutofillShiftKind> SlotKindsOf(bool includeDayShift)
        => includeDayShift ? AllSlotKinds : AllKinds;

    /// <param name="kind">Shift kind</param>
    public static Guid ShiftIdOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => EarlyShiftId,
        AutofillShiftKind.Late => LateShiftId,
        AutofillShiftKind.Night => NightShiftId,
        AutofillShiftKind.Day => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "The day shift exists per order only. The order-less shift triple of the scenarios before scenario 4 has "
            + "no day shift, so ask for it through the order-aware overload."),
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
        return MultiOrderShiftIds[orderIndex - FirstOrderIndex][SlotIndexOf(kind)];
    }

    /// <summary>
    /// Slot kind an id belongs to, or null for an id the catalog does not know. This is the ONLY way
    /// to tell a day shift from a late one: both carry the engine's late shift type index, and the
    /// shift reference id is what separates them.
    /// </summary>
    /// <param name="shiftId">Shift reference id of a token, locked work or demanded slot</param>
    public static AutofillShiftKind? SlotKindOf(Guid shiftId)
        => SlotKindByShiftId.TryGetValue(shiftId, out var kind) ? kind : null;

    /// <summary>
    /// True when the id addresses a day shift of any order. The day shift has no order-less variant,
    /// so an unknown id and an order-less id both answer false.
    /// </summary>
    /// <param name="shiftId">Shift reference id of a token, locked work or demanded slot</param>
    public static bool IsDayShift(Guid shiftId) => SlotKindOf(shiftId) == AutofillShiftKind.Day;

    /// <summary>
    /// Order an id belongs to: <see cref="SingleOrderIndex"/> for the order-less triple, the order
    /// index for one of the nine multi-order ids and <see cref="UnknownOrderIndex"/> for anything the
    /// catalog does not know. It never throws, because the analyzer resolves the order of every token
    /// of every scenario and an unknown id has to stay reportable instead of ending the measurement.
    /// </summary>
    /// <param name="shiftId">Shift reference id of a token, locked work or demanded slot</param>
    public static int OrderOf(Guid shiftId)
        => OrderByShiftId.TryGetValue(shiftId, out var order) ? order : UnknownOrderIndex;

    /// <summary>
    /// Order identity of a shift, the value the engine reads as <c>LocationContext</c>. Never null:
    /// a plan with a single order carries one uniform value, which scores the same as no value at all.
    /// </summary>
    /// <param name="orderIndex">Order the shift belongs to, or <see cref="SingleOrderIndex"/></param>
    public static string LocationContextOf(int orderIndex)
    {
        if (orderIndex != SingleOrderIndex)
        {
            EnsureKnownOrder(orderIndex);
        }

        return OrderIdentities[orderIndex].ToString();
    }

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
        AutofillShiftKind.Day => AutofillSpecConstants.DayShiftName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <param name="kind">Shift kind</param>
    public static string StartTimeOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => AutofillSpecConstants.EarlyStartTime,
        AutofillShiftKind.Late => AutofillSpecConstants.LateStartTime,
        AutofillShiftKind.Night => AutofillSpecConstants.NightStartTime,
        AutofillShiftKind.Day => AutofillSpecConstants.DayStartTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <param name="kind">Shift kind</param>
    public static string EndTimeOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => AutofillSpecConstants.EarlyEndTime,
        AutofillShiftKind.Late => AutofillSpecConstants.LateEndTime,
        AutofillShiftKind.Night => AutofillSpecConstants.NightEndTime,
        AutofillShiftKind.Day => AutofillSpecConstants.DayEndTime,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <summary>Single character used by the plan matrix.</summary>
    /// <param name="kind">Shift kind</param>
    public static char SymbolOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => EarlySymbol,
        AutofillShiftKind.Late => LateSymbol,
        AutofillShiftKind.Night => NightSymbol,
        AutofillShiftKind.Day => DaySymbol,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <summary>
    /// The rotation CLASS of a slot kind: itself for the three classic shifts, late for the day shift.
    /// This is the projection every rule about rotation, purity and fairness applies before it judges.
    /// </summary>
    /// <param name="kind">Slot kind</param>
    public static AutofillShiftKind ShiftClassOf(AutofillShiftKind kind)
        => FromShiftTypeIndex(ShiftTypeIndexOf(kind));

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

    /// <summary>
    /// The engine SHIFT CLASS a kind is expected to map to. The day shift maps to the late index —
    /// <c>ShiftTypeInference</c> classifies 08:00-16:00 as late and a fourth class does not exist — so
    /// this function is deliberately not injective and must never be used to address the pinned id
    /// table. <see cref="SlotIndexOf"/> is the lookup for that.
    /// </summary>
    /// <param name="kind">Shift kind</param>
    public static int ShiftTypeIndexOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early => ShiftTypeInference.EarlyIndex,
        AutofillShiftKind.Late => ShiftTypeInference.LateIndex,
        AutofillShiftKind.Night => ShiftTypeInference.NightIndex,
        AutofillShiftKind.Day => ShiftTypeInference.LateIndex,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

    /// <summary>
    /// Position of a slot kind in the pinned id table and in the slot order of a day. Unlike
    /// <see cref="ShiftTypeIndexOf"/> this IS injective: it is what keeps the day shift's id apart
    /// from the late shift's id although the engine gives both the same class.
    /// </summary>
    /// <param name="kind">Shift kind</param>
    public static int SlotIndexOf(AutofillShiftKind kind) => kind switch
    {
        AutofillShiftKind.Early or AutofillShiftKind.Late or AutofillShiftKind.Night or AutofillShiftKind.Day
            => (int)kind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown shift kind."),
    };

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
                    Priority: 0)
                {
                    LocationContext = LocationContextOf(SingleOrderIndex),
                });
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
    /// <param name="includeDayShift">
    /// True to cut a fourth shift 08:00-16:00 per order, with an id of its own. False keeps the
    /// three-shift cut of scenario 4, so that scenario builds exactly the slots it built before
    /// </param>
    public static IReadOnlyList<CoreShift> BuildDailyShifts(
        DateOnly from, DateOnly until, int orderCount, bool includeDayShift = false)
    {
        EnsureKnownOrder(orderCount);

        var kinds = SlotKindsOf(includeDayShift);
        var shifts = new List<CoreShift>();
        for (var date = from; date <= until; date = date.AddDays(1))
        {
            var iso = date.ToString(AutofillSpecConstants.IsoDateFormat);
            for (var order = FirstOrderIndex; order < FirstOrderIndex + orderCount; order++)
            {
                foreach (var kind in kinds)
                {
                    shifts.Add(new CoreShift(
                        Id: ShiftIdOf(order, kind).ToString(),
                        Name: NameOf(order, kind),
                        Date: iso,
                        StartTime: StartTimeOf(kind),
                        EndTime: EndTimeOf(kind),
                        Hours: AutofillSpecConstants.ShiftHours,
                        RequiredAssignments: 1,
                        Priority: 0)
                    {
                        LocationContext = LocationContextOf(order),
                    });
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
        foreach (var kind in AllSlotKinds)
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
    /// <param name="includeDayShift">True when the scenario cuts a day shift per order</param>
    public static string DescribeAuctionOrderOfOneDay(int orderCount, bool includeDayShift = false)
    {
        EnsureKnownOrder(orderCount);

        var kinds = SlotKindsOf(includeDayShift);
        var slots = new List<(string Id, string StartTime, string Label)>();
        for (var order = FirstOrderIndex; order < FirstOrderIndex + orderCount; order++)
        {
            foreach (var kind in kinds)
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

        problems.AddRange(ValidateDayShiftInference());
        problems.AddRange(ValidateMatrixSymbols());
        return problems;
    }

    /// <summary>
    /// Proves that every state the plan matrix can draw has a glyph of its own. The matrix is read by
    /// eye and by nothing else, so two states sharing a character do not fail anything — they silently
    /// turn one picture into another. Scenario 6 added two markers to a set that already used 'X', and
    /// this check is what makes that collision a red test instead of a misread report.
    /// </summary>
    public static IReadOnlyList<string> ValidateMatrixSymbols()
    {
        var symbols = new List<(string Name, char Symbol)>
        {
            (nameof(FreeSymbol), FreeSymbol),
            (nameof(MultipleShiftsSymbol), MultipleShiftsSymbol),
            (nameof(AbsenceSymbol), AbsenceSymbol),
            (nameof(FreeCommandSymbol), FreeCommandSymbol),
        };
        symbols.AddRange(AllSlotKinds.Select(kind => (kind.ToString(), SymbolOf(kind))));

        return symbols
            .GroupBy(entry => entry.Symbol)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"The matrix symbol '{group.Key}' is used by "
                + string.Join(" and ", group.Select(entry => entry.Name))
                + ". Every state the matrix draws needs a glyph of its own, otherwise the picture stops "
                + "distinguishing them.")
            .ToList();
    }

    /// <summary>
    /// Proves the single fact the whole scenario-5 modelling rests on: the day shift's span
    /// 08:00-16:00 is classified LATE by the production inference. Scenario 5 keeps the day shift as a
    /// slot of its own precisely BECAUSE the engine has no fourth class for it, and every rotation,
    /// purity and fairness expectation of that scenario is written for the three-class view that
    /// follows. Were the inference to grow a day class, the day shift would stop being a late shift
    /// and those expectations would silently measure something else — so this is checked separately
    /// from the loop above, with its own message, rather than folded into a generic kind sweep.
    /// </summary>
    public static IReadOnlyList<string> ValidateDayShiftInference()
    {
        const AutofillShiftKind Kind = AutofillShiftKind.Day;
        var actual = ShiftTypeInference.FromSpanString(StartTimeOf(Kind), EndTimeOf(Kind));
        if (actual == ShiftTypeInference.LateIndex)
        {
            return [];
        }

        return
        [
            $"The day shift ({StartTimeOf(Kind)}-{EndTimeOf(Kind)}) is classified as shift type index "
            + $"{actual.ToString(CultureInfo.InvariantCulture)}, but scenario 5 models it as a slot of its own whose "
            + $"engine CLASS is late (index {ShiftTypeInference.LateIndex.ToString(CultureInfo.InvariantCulture)}). "
            + "ShiftTypeInference changed: the day shift is no longer a late shift, so every rotation, purity and "
            + "fairness expectation written for the three-class view now measures something else.",
        ];
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

    private static Dictionary<Guid, AutofillShiftKind> BuildSlotKindByShiftId()
    {
        var byId = new Dictionary<Guid, AutofillShiftKind>
        {
            [EarlyShiftId] = AutofillShiftKind.Early,
            [LateShiftId] = AutofillShiftKind.Late,
            [NightShiftId] = AutofillShiftKind.Night,
        };

        for (var order = FirstOrderIndex; order <= MaxOrderCount; order++)
        {
            foreach (var kind in AllSlotKinds)
            {
                byId[MultiOrderShiftIds[order - FirstOrderIndex][SlotIndexOf(kind)]] = kind;
            }
        }

        return byId;
    }
}
