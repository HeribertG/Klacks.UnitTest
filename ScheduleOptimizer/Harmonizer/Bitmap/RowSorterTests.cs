// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using NUnit.Framework;

namespace Klacks.UnitTest.ScheduleOptimizer.Harmonizer.Bitmap;

[TestFixture]
public class RowSorterTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Sort_ByTargetHoursDescending_AsPrimaryCriterion()
    {
        var agents = new List<BitmapAgent>
        {
            new("low", "Low Hours", 50m, new HashSet<CellSymbol>()),
            new("high", "High Hours", 200m, new HashSet<CellSymbol>()),
            new("mid", "Mid Hours", 100m, new HashSet<CellSymbol>()),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));

        var sorted = RowSorter.Sort(bitmap);

        sorted.Rows[0].Id.ShouldBe("high");
        sorted.Rows[1].Id.ShouldBe("mid");
        sorted.Rows[2].Id.ShouldBe("low");
    }

    [Test]
    public void Sort_TieOnHours_BreaksByPreferredMatchesDescending()
    {
        var preferEarly = new HashSet<CellSymbol> { CellSymbol.Early };
        var agents = new List<BitmapAgent>
        {
            new("a", "A no-prefs", 100m, new HashSet<CellSymbol>()),
            new("b", "B early-prefs", 100m, preferEarly),
        };
        var assignments = new List<BitmapAssignment>
        {
            new("b", Day0, CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
            new("b", Day0.AddDays(1), CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), assignments));

        var sorted = RowSorter.Sort(bitmap);

        sorted.Rows[0].Id.ShouldBe("b");
        sorted.Rows[1].Id.ShouldBe("a");
    }

    [Test]
    public void Sort_TieOnAllCriteria_BreaksByAgentIdStable()
    {
        var agents = new List<BitmapAgent>
        {
            new("zeta", "Z", 100m, new HashSet<CellSymbol>()),
            new("alpha", "A", 100m, new HashSet<CellSymbol>()),
            new("mid", "M", 100m, new HashSet<CellSymbol>()),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));

        var sorted = RowSorter.Sort(bitmap);

        sorted.Rows[0].Id.ShouldBe("alpha");
        sorted.Rows[1].Id.ShouldBe("mid");
        sorted.Rows[2].Id.ShouldBe("zeta");
    }

    [Test]
    public void Sort_PreservesCellAssignmentsAlongsideRowReorder()
    {
        var agents = new List<BitmapAgent>
        {
            new("low", "Low", 50m, new HashSet<CellSymbol>()),
            new("high", "High", 200m, new HashSet<CellSymbol>()),
        };
        var highWorkId = Guid.NewGuid();
        var lowWorkId = Guid.NewGuid();
        var assignments = new List<BitmapAssignment>
        {
            new("high", Day0, CellSymbol.Early, Guid.NewGuid(), [highWorkId], false),
            new("low", Day0, CellSymbol.Late, Guid.NewGuid(), [lowWorkId], false),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), assignments));

        var sorted = RowSorter.Sort(bitmap);

        sorted.GetCell(0, 0).WorkIds.ShouldContain(highWorkId);
        sorted.GetCell(1, 0).WorkIds.ShouldContain(lowWorkId);
    }
}
