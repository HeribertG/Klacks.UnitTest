// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Bitmap;

[TestFixture]
public class BitmapBuilderTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Build_NoAssignments_AllCellsAreFree()
    {
        var bitmap = BitmapBuilder.Build(BuildInput(rows: 2, days: 3, []));

        for (var r = 0; r < bitmap.RowCount; r++)
        {
            for (var d = 0; d < bitmap.DayCount; d++)
            {
                bitmap.GetCell(r, d).Symbol.ShouldBe(CellSymbol.Free);
            }
        }
    }

    [Test]
    public void Build_SingleAssignment_PopulatesOneCell()
    {
        var workId = Guid.NewGuid();
        var shiftId = Guid.NewGuid();
        var assignment = new BitmapAssignment(
            "agent-0",
            Day0.AddDays(1),
            CellSymbol.Late,
            shiftId,
            [workId],
            false,
            Day0.AddDays(1).ToDateTime(new TimeOnly(14, 0)),
            Day0.AddDays(1).ToDateTime(new TimeOnly(22, 0)),
            8m);

        var bitmap = BitmapBuilder.Build(BuildInput(rows: 1, days: 3, [assignment]));

        var cell = bitmap.GetCell(0, 1);
        cell.Symbol.ShouldBe(CellSymbol.Late);
        cell.ShiftRefId.ShouldBe(shiftId);
        cell.WorkIds.ShouldContain(workId);
        cell.Hours.ShouldBe(8m);
    }

    [Test]
    public void Build_MultipleAssignmentsSameDay_MergesWorkIdsAndHours()
    {
        var earlyWorkId = Guid.NewGuid();
        var lateWorkId = Guid.NewGuid();
        var assignments = new List<BitmapAssignment>
        {
            new("agent-0", Day0, CellSymbol.Early, Guid.NewGuid(), [earlyWorkId], false,
                Day0.ToDateTime(new TimeOnly(6, 0)), Day0.ToDateTime(new TimeOnly(10, 0)), 4m),
            new("agent-0", Day0, CellSymbol.Late, Guid.NewGuid(), [lateWorkId], false,
                Day0.ToDateTime(new TimeOnly(14, 0)), Day0.ToDateTime(new TimeOnly(18, 0)), 4m),
        };

        var bitmap = BitmapBuilder.Build(BuildInput(rows: 1, days: 1, assignments));

        var cell = bitmap.GetCell(0, 0);
        cell.WorkIds.Count.ShouldBe(2);
        cell.WorkIds.ShouldContain(earlyWorkId);
        cell.WorkIds.ShouldContain(lateWorkId);
        cell.Hours.ShouldBe(8m);
        cell.StartAt.Hour.ShouldBe(6);
        cell.EndAt.Hour.ShouldBe(18);
    }

    [Test]
    public void Build_AnyContributingLocked_PropagatesLockToCell()
    {
        var assignments = new List<BitmapAssignment>
        {
            new("agent-0", Day0, CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
                Day0.ToDateTime(new TimeOnly(6, 0)), Day0.ToDateTime(new TimeOnly(10, 0)), 4m),
            new("agent-0", Day0, CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], true,
                Day0.ToDateTime(new TimeOnly(14, 0)), Day0.ToDateTime(new TimeOnly(18, 0)), 4m),
        };

        var bitmap = BitmapBuilder.Build(BuildInput(rows: 1, days: 1, assignments));

        bitmap.GetCell(0, 0).IsLocked.ShouldBeTrue();
    }

    [Test]
    public void Build_BreakDominatesCollidingWork_AndForcesLock()
    {
        var workId = Guid.NewGuid();
        var breakId = Guid.NewGuid();
        var assignments = new List<BitmapAssignment>
        {
            new("agent-0", Day0, CellSymbol.Early, Guid.NewGuid(), [workId], false,
                Day0.ToDateTime(new TimeOnly(6, 0)), Day0.ToDateTime(new TimeOnly(14, 0)), 8m),
            new("agent-0", Day0, CellSymbol.Break, Guid.Empty, [breakId], true,
                default, default, 8m),
        };

        var bitmap = BitmapBuilder.Build(BuildInput(rows: 1, days: 1, assignments));

        var cell = bitmap.GetCell(0, 0);
        cell.Symbol.ShouldBe(CellSymbol.Break);
        cell.IsLocked.ShouldBeTrue();
        cell.WorkIds.ShouldContain(breakId);
        cell.Hours.ShouldBe(16m);
    }

    [Test]
    public void Build_BreakOnly_IsLockedAndCarriesBreakHours()
    {
        var breakId = Guid.NewGuid();
        var assignment = new BitmapAssignment(
            "agent-0", Day0, CellSymbol.Break, Guid.Empty, [breakId], true,
            default, default, 7.6m);

        var bitmap = BitmapBuilder.Build(BuildInput(rows: 1, days: 1, [assignment]));

        var cell = bitmap.GetCell(0, 0);
        cell.Symbol.ShouldBe(CellSymbol.Break);
        cell.IsLocked.ShouldBeTrue();
        cell.Hours.ShouldBe(7.6m);
    }

    [Test]
    public void Build_AssignmentForUnknownAgentOrOutOfRangeDate_IsIgnored()
    {
        var assignments = new List<BitmapAssignment>
        {
            new("ghost", Day0, CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("agent-0", Day0.AddDays(99), CellSymbol.Late, Guid.NewGuid(), [Guid.NewGuid()], false),
        };

        var bitmap = BitmapBuilder.Build(BuildInput(rows: 1, days: 3, assignments));

        for (var d = 0; d < bitmap.DayCount; d++)
        {
            bitmap.GetCell(0, d).Symbol.ShouldBe(CellSymbol.Free);
        }
    }

    [Test]
    public void Build_EndDateBeforeStartDate_Throws()
    {
        var input = new BitmapInput(
            [new BitmapAgent("a", "A", 100m, new HashSet<CellSymbol>())],
            Day0.AddDays(2),
            Day0,
            []);

        Should.Throw<ArgumentException>(() => BitmapBuilder.Build(input));
    }

    private static BitmapInput BuildInput(int rows, int days, IReadOnlyList<BitmapAssignment> assignments)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>()));
        }
        return new BitmapInput(agents, Day0, Day0.AddDays(days - 1), assignments);
    }
}
