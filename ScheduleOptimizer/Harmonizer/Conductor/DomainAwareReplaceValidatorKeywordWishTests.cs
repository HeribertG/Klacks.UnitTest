// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Constraints.Keywords;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

/// <summary>
/// Keyword wishes in Wizard 2/3/4: Hard mode keeps the FREE / OnlyX / NoX vetoes; Soft mode drops them; in both modes a
/// move must not raise a row's keyword-wish penalty, so a -FREE or OnlyX row never loses its wanted shift and a Soft move
/// never breaks more wishes.
/// </summary>
[TestFixture]
public class DomainAwareReplaceValidatorKeywordWishTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);
    private const int Day = 1;

    private static Cell Shift(CellSymbol symbol, int hour)
    {
        var date = Day0.AddDays(Day);
        var start = date.ToDateTime(new TimeOnly(hour, 0));
        return new Cell(symbol, Guid.NewGuid(), [Guid.NewGuid()], false, start, start.AddHours(8), 8m);
    }

    private static HarmonyBitmap Bitmap(Cell row0, Cell row1)
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "agent-0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "agent-1", 100m, new HashSet<CellSymbol>()),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));
        bitmap.SetCell(0, Day, row0);
        bitmap.SetCell(1, Day, row1);
        return bitmap;
    }

    private static Dictionary<(string AgentId, DateOnly Date), DayAvailability> Availability(
        string agentId, params ScheduleCommandKeyword[] keywords)
    {
        var day = KeywordDay.From(keywords)!;
        return new Dictionary<(string AgentId, DateOnly Date), DayAvailability>
        {
            [(agentId, Day0.AddDays(Day))] = new DayAvailability(
                WorksOnDay: true,
                HasFreeCommand: day.Combined == ScheduleCommandKeyword.Free,
                HasBreakBlocker: false,
                Keywords: day),
        };
    }

    [Test]
    public void HardMode_FreeDayReceivingAShift_IsRejected()
    {
        var bitmap = Bitmap(Shift(CellSymbol.Early, 7), Cell.Free());
        var validator = new DomainAwareReplaceValidator(Availability("agent-1", ScheduleCommandKeyword.Free));

        validator.Diagnose(bitmap, new ReplaceMove(0, 1, Day)).ShouldNotBeNull();
    }

    [Test]
    public void SoftMode_NoVeto_ButAMoveOntoAFreeDayRaisesThePenalty_AndIsRejected()
    {
        var bitmap = Bitmap(Shift(CellSymbol.Early, 7), Cell.Free());
        var validator = new DomainAwareReplaceValidator(
            Availability("agent-1", ScheduleCommandKeyword.Free), keywordMode: KeywordMode.Soft);

        validator.DiagnoseDayRestrictions(bitmap.Rows[1], Shift(CellSymbol.Early, 7), Day0.AddDays(Day), "rowB").ShouldBeNull();
        validator.Diagnose(bitmap, new ReplaceMove(0, 1, Day))!.ShouldContain("wishes");
    }

    [Test]
    public void SoftMode_MovingAShiftOffABrokenFreeDay_IsAllowed()
    {
        var bitmap = Bitmap(Cell.Free(), Shift(CellSymbol.Early, 7));
        var validator = new DomainAwareReplaceValidator(
            Availability("agent-1", ScheduleCommandKeyword.Free), keywordMode: KeywordMode.Soft);

        validator.Diagnose(bitmap, new ReplaceMove(0, 1, Day)).ShouldBeNull();
    }

    [Test]
    public void SoftMode_SwapThatTradesABrokenOnlyForAFulfilledOnly_IsAllowed()
    {
        var bitmap = Bitmap(Shift(CellSymbol.Night, 23), Shift(CellSymbol.Early, 7));
        var validator = new DomainAwareReplaceValidator(
            Availability("agent-1", ScheduleCommandKeyword.OnlyNight), keywordMode: KeywordMode.Soft);

        validator.Diagnose(bitmap, new ReplaceMove(0, 1, Day)).ShouldBeNull();
    }

    [TestCase(KeywordMode.Hard)]
    [TestCase(KeywordMode.Soft)]
    public void BothModes_TakingTheShiftAwayFromANotFreeDay_IsRejected(KeywordMode mode)
    {
        var bitmap = Bitmap(Cell.Free(), Shift(CellSymbol.Late, 15));
        var validator = new DomainAwareReplaceValidator(
            Availability("agent-1", ScheduleCommandKeyword.NotFree), keywordMode: mode);

        validator.Diagnose(bitmap, new ReplaceMove(0, 1, Day))!.ShouldContain("wishes");
    }

    [Test]
    public void HardMode_OnlyNightRowGivingAwayItsNightForAFreeCell_IsRejected()
    {
        var bitmap = Bitmap(Cell.Free(), Shift(CellSymbol.Night, 23));
        var validator = new DomainAwareReplaceValidator(Availability("agent-1", ScheduleCommandKeyword.OnlyNight));

        validator.IsValid(bitmap, new ReplaceMove(0, 1, Day)).ShouldBeFalse();
    }

    [Test]
    public void ForInput_TakesTheKeywordModeOfTheInput()
    {
        var bitmap = Bitmap(Shift(CellSymbol.Early, 7), Shift(CellSymbol.Late, 15));
        var input = new BitmapInput(
            bitmap.Rows, Day0, Day0.AddDays(2), [],
            Availability: Availability("agent-1", ScheduleCommandKeyword.NoEarly),
            KeywordMode: KeywordMode.Soft);

        var validator = DomainAwareReplaceValidator.ForInput(input, null);

        validator.DiagnoseDayRestrictions(bitmap.Rows[1], Shift(CellSymbol.Early, 7), Day0.AddDays(Day), "rowB").ShouldBeNull();
    }

    [TestCase(CellSymbol.Late, CellSymbol.Night, true)]
    [TestCase(CellSymbol.Night, CellSymbol.Late, false)]
    public void CrossDaySwap_InWizard3_IsGuardedByTheRowsWishPenalty(CellSymbol onlyNightRowHas, CellSymbol otherRowOffers, bool allowed)
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "agent-0", 100m, new HashSet<CellSymbol>()),
            new("agent-1", "agent-1", 100m, new HashSet<CellSymbol>()),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));
        bitmap.SetCell(0, 2, CellOn(Day0.AddDays(2), otherRowOffers));
        bitmap.SetCell(1, Day, CellOn(Day0.AddDays(Day), onlyNightRowHas));
        var validator = new PlanMutationValidator(new DomainAwareReplaceValidator(
            Availability("agent-1", ScheduleCommandKeyword.OnlyNight), keywordMode: KeywordMode.Soft));

        var rejection = validator.Validate(bitmap, new PlanCellSwap(RowA: 0, DayA: 2, RowB: 1, DayB: Day, Reason: "test"));

        (rejection is null).ShouldBe(allowed);
    }

    private static Cell CellOn(DateOnly date, CellSymbol symbol)
    {
        var start = date.ToDateTime(new TimeOnly(symbol == CellSymbol.Night ? 23 : 15, 0));
        return new Cell(symbol, Guid.NewGuid(), [Guid.NewGuid()], false, start, start.AddHours(8), 8m);
    }
}
