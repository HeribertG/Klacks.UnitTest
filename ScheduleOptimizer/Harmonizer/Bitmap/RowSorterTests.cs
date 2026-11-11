// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

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

    [Test]
    public void Sort_StandIn_ComesAfterEveryRealPerson_EvenOneWithZeroTargetHours()
    {
        var agents = new List<BitmapAgent>
        {
            new("standin", "Stand-in", 200m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("zero", "Zero Hours", 0m, new HashSet<CellSymbol>()),
            new("real", "Real", 100m, new HashSet<CellSymbol>()),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));

        var sorted = RowSorter.Sort(bitmap);

        sorted.Rows.Select(r => r.Id).ShouldBe(["real", "zero", "standin"]);
    }

    [Test]
    public void Sort_StandInsAmongThemselves_KeepTheOrderByHoursThenId()
    {
        var agents = new List<BitmapAgent>
        {
            new("standin-b", "Stand-in B", 0m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("standin-low", "Stand-in Low", 10m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("standin-a", "Stand-in A", 0m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("standin-high", "Stand-in High", 40m, new HashSet<CellSymbol>(), IsStandIn: true),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));

        var sorted = RowSorter.Sort(bitmap);

        sorted.Rows.Select(r => r.Id).ShouldBe(["standin-high", "standin-low", "standin-a", "standin-b"]);
    }

    [Test]
    public void KeepIncomingOrder_KeepsUserOrder_StandInsLast()
    {
        var preferEarly = new HashSet<CellSymbol> { CellSymbol.Early };
        var agents = new List<BitmapAgent>
        {
            new("standin", "Stand-in", 200m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("low", "Low", 10m, new HashSet<CellSymbol>()),
            new("high", "High", 200m, preferEarly),
            new("mid", "Mid", 100m, new HashSet<CellSymbol>()),
        };
        var assignments = new List<BitmapAssignment>
        {
            new("high", Day0, CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), assignments));

        var sorted = RowSorter.Sort(bitmap, keepIncomingOrder: true);

        sorted.Rows.Select(r => r.Id).ShouldBe(["low", "high", "mid", "standin"]);
        sorted.GetCell(1, 0).Symbol.ShouldBe(CellSymbol.Early);
    }

    [Test]
    public void KeepIncomingOrder_IsStable_WithEqualTargetHours()
    {
        var agents = new List<BitmapAgent>
        {
            new("zeta", "Z", 100m, new HashSet<CellSymbol>()),
            new("standin-b", "Stand-in B", 100m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("alpha", "A", 100m, new HashSet<CellSymbol>()),
            new("standin-a", "Stand-in A", 100m, new HashSet<CellSymbol>(), IsStandIn: true),
            new("mid", "M", 100m, new HashSet<CellSymbol>()),
        };
        var bitmap = BitmapBuilder.Build(new BitmapInput(agents, Day0, Day0.AddDays(2), []));

        var sorted = RowSorter.Sort(bitmap, keepIncomingOrder: true);

        sorted.Rows.Select(r => r.Id).ShouldBe(["zeta", "alpha", "mid", "standin-b", "standin-a"]);
    }
}
