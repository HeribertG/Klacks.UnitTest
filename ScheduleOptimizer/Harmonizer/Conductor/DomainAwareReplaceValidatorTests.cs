// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Conductor;

[TestFixture]
public class DomainAwareReplaceValidatorTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void IsValid_LockedCell_Rejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], true));
        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_ReceivingAgentNotAvailable_Rejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3, agentBuilder: (id, _) => new BitmapAgent(id, id, 100m, new HashSet<CellSymbol>()));
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false, Day0.AddDays(1).ToDateTime(new TimeOnly(7, 0)), Day0.AddDays(1).ToDateTime(new TimeOnly(15, 0)), 8m));

        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>
        {
            [("agent-0", Day0.AddDays(1))] = DayAvailability.AlwaysAvailable,
            [("agent-1", Day0.AddDays(1))] = new DayAvailability(WorksOnDay: false, HasFreeCommand: false, HasBreakBlocker: false),
        };
        var validator = new DomainAwareReplaceValidator(availability);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_BlacklistedShiftAtReceiver_Rejected()
    {
        var shiftId = Guid.NewGuid();
        var bitmap = BuildBitmap(rows: 2, days: 3, agentBuilder: (id, idx) => new BitmapAgent(
            id,
            id,
            100m,
            new HashSet<CellSymbol>(),
            BlacklistedShiftIds: idx == 1 ? new HashSet<Guid> { shiftId } : null));
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Early, shiftId, [Guid.NewGuid()], false, Day0.AddDays(1).ToDateTime(new TimeOnly(7, 0)), Day0.AddDays(1).ToDateTime(new TimeOnly(15, 0)), 8m));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_ExceedsMaxConsecutiveDays_Rejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 5, agentBuilder: (id, idx) => new BitmapAgent(
            id,
            id,
            100m,
            new HashSet<CellSymbol>(),
            MaxConsecutiveDays: idx == 1 ? 3 : 0));
        var workCell = (DateOnly d) => new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            d.ToDateTime(new TimeOnly(7, 0)), d.ToDateTime(new TimeOnly(15, 0)), 8m);
        bitmap.SetCell(1, 0, workCell(Day0));
        bitmap.SetCell(1, 1, workCell(Day0.AddDays(1)));
        bitmap.SetCell(1, 3, workCell(Day0.AddDays(3)));
        bitmap.SetCell(0, 2, workCell(Day0.AddDays(2)));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 2)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_ExceedsMaxWeeklyHours_Rejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 7, agentBuilder: (id, idx) => new BitmapAgent(
            id,
            id,
            100m,
            new HashSet<CellSymbol>(),
            MaxWeeklyHours: idx == 1 ? 40m : 0m));
        var workCell = (DateOnly d) => new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            d.ToDateTime(new TimeOnly(7, 0)), d.ToDateTime(new TimeOnly(15, 0)), 8m);

        for (var d = 0; d < 5; d++)
        {
            bitmap.SetCell(1, d, workCell(Day0.AddDays(d)));
        }
        bitmap.SetCell(0, 5, workCell(Day0.AddDays(5)));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 5)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_ViolatesMinPause_Rejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3, agentBuilder: (id, idx) => new BitmapAgent(
            id,
            id,
            100m,
            new HashSet<CellSymbol>(),
            MinPauseHours: idx == 1 ? 11m : 0m));

        bitmap.SetCell(1, 0, new Cell(
            CellSymbol.Late,
            Guid.NewGuid(),
            [Guid.NewGuid()],
            false,
            Day0.ToDateTime(new TimeOnly(14, 0)),
            Day0.ToDateTime(new TimeOnly(22, 0)),
            8m));
        bitmap.SetCell(0, 1, new Cell(
            CellSymbol.Early,
            Guid.NewGuid(),
            [Guid.NewGuid()],
            false,
            Day0.AddDays(1).ToDateTime(new TimeOnly(6, 0)),
            Day0.AddDays(1).ToDateTime(new TimeOnly(14, 0)),
            8m));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_BreakCellOnReceivingSide_Rejected()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            Day0.AddDays(1).ToDateTime(new TimeOnly(7, 0)), Day0.AddDays(1).ToDateTime(new TimeOnly(15, 0)), 8m));
        bitmap.SetCell(1, 1, new Cell(CellSymbol.Break, null, [Guid.NewGuid()], true, default, default, 8m));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeFalse();
    }

    [Test]
    public void IsValid_BreakInterruptsConsecutiveRun_Accepted()
    {
        var bitmap = BuildBitmap(rows: 2, days: 6, agentBuilder: (id, idx) => new BitmapAgent(
            id, id, 100m, new HashSet<CellSymbol>(), MaxConsecutiveDays: idx == 1 ? 3 : 0));
        var workCell = (DateOnly d) => new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            d.ToDateTime(new TimeOnly(7, 0)), d.ToDateTime(new TimeOnly(15, 0)), 8m);

        bitmap.SetCell(1, 0, workCell(Day0));
        bitmap.SetCell(1, 1, workCell(Day0.AddDays(1)));
        bitmap.SetCell(1, 2, new Cell(CellSymbol.Break, null, [Guid.NewGuid()], true, default, default, 8m));
        bitmap.SetCell(1, 4, workCell(Day0.AddDays(4)));
        bitmap.SetCell(0, 3, workCell(Day0.AddDays(3)));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 3)).ShouldBeTrue();
    }

    [Test]
    public void IsValid_BreakHoursDoNotCountTowardWeeklyMax()
    {
        var bitmap = BuildBitmap(rows: 2, days: 7, agentBuilder: (id, idx) => new BitmapAgent(
            id, id, 100m, new HashSet<CellSymbol>(), MaxWeeklyHours: idx == 1 ? 40m : 0m));
        var workCell = (DateOnly d) => new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
            d.ToDateTime(new TimeOnly(7, 0)), d.ToDateTime(new TimeOnly(15, 0)), 8m);

        bitmap.SetCell(1, 0, workCell(Day0));
        bitmap.SetCell(1, 1, workCell(Day0.AddDays(1)));
        bitmap.SetCell(1, 2, workCell(Day0.AddDays(2)));
        bitmap.SetCell(1, 3, workCell(Day0.AddDays(3)));
        bitmap.SetCell(1, 4, new Cell(CellSymbol.Break, null, [Guid.NewGuid()], true, default, default, 8m));
        bitmap.SetCell(0, 5, workCell(Day0.AddDays(5)));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 5)).ShouldBeTrue();
    }

    [Test]
    public void IsValid_NormalSwap_Accepted()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 1, new Cell(
            CellSymbol.Early,
            Guid.NewGuid(),
            [Guid.NewGuid()],
            false,
            Day0.AddDays(1).ToDateTime(new TimeOnly(7, 0)),
            Day0.AddDays(1).ToDateTime(new TimeOnly(15, 0)),
            8m));

        var validator = new DomainAwareReplaceValidator(null);

        validator.IsValid(bitmap, new ReplaceMove(0, 1, 1)).ShouldBeTrue();
    }

    private static HarmonyBitmap BuildBitmap(int rows, int days, Func<string, int, BitmapAgent>? agentBuilder = null)
    {
        agentBuilder ??= (id, _) => new BitmapAgent(id, id, 100m, new HashSet<CellSymbol>());
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(agentBuilder($"agent-{r}", r));
        }
        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }
}
