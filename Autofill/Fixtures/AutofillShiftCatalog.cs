// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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

    /// <summary>Matrix symbol of an unused day.</summary>
    public const char FreeSymbol = '.';

    /// <summary>Matrix symbol of a day carrying more than one shift for the same employee.</summary>
    public const char DoubleBookedSymbol = 'X';

    private const char EarlySymbol = 'F';
    private const char LateSymbol = 'S';
    private const char NightSymbol = 'N';

    private static readonly AutofillShiftKind[] AllKinds =
        [AutofillShiftKind.Early, AutofillShiftKind.Late, AutofillShiftKind.Night];

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
}
