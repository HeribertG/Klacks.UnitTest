// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// The candidate budget is global across the whole period. Enumerating strictly day-major spent it on
/// the first days and never reached the rest of the period, so no swap past that point was ever
/// considered. These tests pin the rotating start day at the level where it is decided - the
/// enumeration - by recording which days the validator is offered. The conductor's own wiring of the
/// start day is exercised by the conductor smoke tests.
/// </summary>
[TestFixture]
public sealed class ReplaceMutationCandidateSpreadTests
{
    private const int DayCount = 10;
    private const int RowCount = 4;
    private const int TinyBudget = 2;
    private static readonly DateOnly PeriodStart = new(2026, 3, 2);

    [Test]
    public void FindBestMove_WithoutStartDay_StartsAtTheFirstDay()
    {
        var recorder = new RecordingValidator();
        var mutation = new ReplaceMutation(new HarmonyScorer(), recorder, TinyBudget);

        mutation.FindBestMove(BuildAlternatingBitmap(), primaryRow: 0, lockedRows: new HashSet<int>());

        recorder.Days.ShouldBe([0]);
    }

    [Test]
    public void FindBestMove_WithStartDay_EnumeratesThatDayInsteadOfTheFirst()
    {
        const int startDay = 5;
        var recorder = new RecordingValidator();
        var mutation = new ReplaceMutation(new HarmonyScorer(), recorder, TinyBudget);

        mutation.FindBestMove(BuildAlternatingBitmap(), primaryRow: 0, lockedRows: new HashSet<int>(), startDay);

        recorder.Days.ShouldBe([startDay]);
    }

    [Test]
    public void FindBestMove_StartDayPastTheEnd_WrapsAround()
    {
        var recorder = new RecordingValidator();
        var mutation = new ReplaceMutation(new HarmonyScorer(), recorder, TinyBudget);

        mutation.FindBestMove(BuildAlternatingBitmap(), primaryRow: 0, lockedRows: new HashSet<int>(), DayCount - 1);

        recorder.Days.ShouldBe([DayCount - 1]);
    }

    [Test]
    public void FindBestMove_BudgetSpanningSeveralDays_ContinuesPastThePeriodEnd()
    {
        // Budget for four candidates, three per day: the window starts on the last day and must wrap to
        // day 0 rather than stopping at the period end.
        const int budget = 4;
        var recorder = new RecordingValidator();
        var mutation = new ReplaceMutation(new HarmonyScorer(), recorder, budget);

        mutation.FindBestMove(BuildAlternatingBitmap(), primaryRow: 0, lockedRows: new HashSet<int>(), DayCount - 1);

        recorder.Days.ShouldBe([DayCount - 1, 0]);
    }

    private static HarmonyBitmap BuildAlternatingBitmap()
    {
        var agents = new List<BitmapAgent>(RowCount);
        for (var r = 0; r < RowCount; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"agent-{r}", 100m, new HashSet<CellSymbol>()));
        }

        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, PeriodStart, PeriodStart.AddDays(DayCount - 1), []));

        // Every partner differs from the primary row on every day, so each day offers RowCount-1
        // candidates and the budget - not the data - decides how far the enumeration reaches.
        for (var day = 0; day < DayCount; day++)
        {
            bitmap.SetCell(0, day, WorkCell(CellSymbol.Early, PeriodStart.AddDays(day)));
            for (var row = 1; row < RowCount; row++)
            {
                bitmap.SetCell(row, day, WorkCell(CellSymbol.Late, PeriodStart.AddDays(day)));
            }
        }

        return bitmap;
    }

    private static Cell WorkCell(CellSymbol symbol, DateOnly day) => new(
        symbol,
        Guid.NewGuid(),
        [Guid.NewGuid()],
        false,
        day.ToDateTime(new TimeOnly(7, 0)),
        day.ToDateTime(new TimeOnly(15, 0)),
        8m);

    /// <summary>Records the day of every enumerated candidate and rejects them all, so the enumeration
    /// order is observable without any dependency on scores.</summary>
    private sealed class RecordingValidator : IReplaceValidator
    {
        private readonly List<int> _days = [];

        public IReadOnlyList<int> Days => _days;

        public bool IsValid(HarmonyBitmap bitmap, ReplaceMove move)
        {
            if (_days.Count == 0 || _days[^1] != move.Day)
            {
                _days.Add(move.Day);
            }

            return false;
        }
    }
}
