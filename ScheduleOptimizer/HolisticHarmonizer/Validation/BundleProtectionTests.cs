// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Validation;

/// <summary>
/// The stage-3 protection refuses a batch when it raises the number of mixed packages (packages with more than one shift
/// symbol) summed over the touched rows, fewer or the same number is allowed; and it measures a package that runs into
/// the period from the days before it with its full length. Rows are written as one character per day: E early, L late,
/// N night, dot free.
/// </summary>
[TestFixture]
public class BundleProtectionTests
{
    private const decimal ShiftHours = 8m;
    private const string FirstRowId = "a0";
    private const string MixedReason = "mixed packages";
    private const string LongReason = "beyond the ideal length";
    private const string HoursReason = "loses hours";
    private static readonly TimeOnly EarlyStart = new(6, 0);
    private static readonly TimeOnly LateEnd = new(22, 0);
    private const int CarriedDays = 3;
    private static readonly DateOnly FirstDay = new(2026, 3, 2);

    [Test]
    public void Swap_MakingPurePackagesMixed_IsRefused()
    {
        var bitmap = Build("EEEE", "LLLL");
        var swap = new PlanCellSwap(0, 1, 1, 1, string.Empty);

        Diagnose(new BundleProtection(), bitmap, swap).ShouldNotBeNull().ShouldContain(MixedReason);
    }

    [Test]
    public void Swap_MakingMixedPackagesPure_IsAllowed()
    {
        var bitmap = Build("EELE", "LLEL");
        var swap = new PlanCellSwap(0, 2, 1, 2, string.Empty);

        Diagnose(new BundleProtection(), bitmap, swap).ShouldBeNull();
    }

    [Test]
    public void Swap_LeavingTheNumberOfMixedPackagesEqual_IsAllowed()
    {
        var bitmap = Build("EELE", "EEEE");
        var swap = new PlanCellSwap(0, 2, 1, 2, string.Empty);

        Diagnose(new BundleProtection(), bitmap, swap).ShouldBeNull();
    }

    [Test]
    public void Measure_CountsAPackageAsMixedOnlyWithinItsOwnWorkedDays()
    {
        var bitmap = Build("EL.LL");

        var shape = RowShape.Measure(bitmap, [0], new Dictionary<string, PeriodCarryIn>()).Single();

        shape.Packages.ShouldBe(2);
        shape.MixedPackages.ShouldBe(1);
    }

    [Test]
    public void Swap_GrowingAPackageFromThePreviousPeriodBeyondTheIdeal_IsRefused()
    {
        var bitmap = Build("EE.E", "..E.");
        var swap = new PlanCellSwap(0, 2, 1, 2, string.Empty);
        var protection = new BundleProtection(PeriodCarryIn.Of(EarlyDaysBefore(FirstRowId, CarriedDays), FirstDay));

        Diagnose(protection, bitmap, swap).ShouldNotBeNull().ShouldContain(LongReason);
    }

    [Test]
    public void Swap_GrowingAPackageInsideThePeriodOnly_IsAllowed()
    {
        var bitmap = Build("EE.E", "..E.");
        var swap = new PlanCellSwap(0, 2, 1, 2, string.Empty);

        Diagnose(new BundleProtection(), bitmap, swap).ShouldBeNull();
    }

    [Test]
    public void Measure_APackageRunningIntoThePeriod_CountsItsDaysBeforeThePeriod()
    {
        var bitmap = Build("E...");
        var carryIn = PeriodCarryIn.Of(EarlyDaysBefore(FirstRowId, CarriedDays), FirstDay);

        var shape = RowShape.Measure(bitmap, [0], carryIn).Single();

        shape.Packages.ShouldBe(1);
        shape.ShortPackages.ShouldBe(0);
    }

    [Test]
    public void CarryIn_CountsOnlyTheWorkedDaysRightBeforeThePeriod()
    {
        var assignments = new[]
        {
            Before(FirstRowId, 1, CellSymbol.Early),
            Before(FirstRowId, 2, CellSymbol.Late),
            Before(FirstRowId, 4, CellSymbol.Early),
            Before(FirstRowId, 0, CellSymbol.Night),
        };

        var carryIn = PeriodCarryIn.Of(assignments, FirstDay)[FirstRowId];

        carryIn.Days.ShouldBe(2);
        carryIn.IsMixed.ShouldBeTrue();
        carryIn.LastSymbol.ShouldBe(CellSymbol.Early);
    }

    [Test]
    public void Swap_GivingHoursOfAHigherRankToALowerRank_IsRefused()
    {
        var bitmap = Build("EE.", "..E");
        var swap = new PlanCellSwap(0, 1, 1, 1, string.Empty);

        Diagnose(new BundleProtection(), bitmap, swap).ShouldNotBeNull().ShouldContain(HoursReason);
    }

    [Test]
    public void Swap_GivingHoursOfALowerRankToAHigherRank_IsAllowed()
    {
        var bitmap = Build("..E", "EE.");
        var swap = new PlanCellSwap(0, 1, 1, 1, string.Empty);

        Diagnose(new BundleProtection(), bitmap, swap).ShouldBeNull();
    }

    [Test]
    public void Measure_ARestShorterThanTwoDaysAfterTheLastWorkBeforeThePeriod_CountsAsShortRest()
    {
        var bitmap = Build(".E..");
        var start = FirstDay.AddDays(1).ToDateTime(EarlyStart);
        bitmap.SetCell(0, 1, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false, start, start.AddHours((double)ShiftHours), ShiftHours));
        var lateEnd = FirstDay.AddDays(-1).ToDateTime(LateEnd);
        var lastWork = Before(FirstRowId, 1, CellSymbol.Late) with { StartAt = lateEnd.AddHours(-(double)ShiftHours), EndAt = lateEnd };

        var withCarryIn = RowShape.Measure(bitmap, [0], PeriodCarryIn.Of([lastWork], FirstDay)).Single();
        var withoutCarryIn = RowShape.Measure(bitmap, [0], new Dictionary<string, PeriodCarryIn>()).Single();

        withCarryIn.ShortRests.ShouldBe(1);
        withoutCarryIn.ShortRests.ShouldBe(0);
    }

    [TestCase(CellSymbol.Late, CellSymbol.Early)]
    [TestCase(CellSymbol.Early, CellSymbol.Late)]
    public void CarryIn_TwoKindsOnTheSameDay_AreMixedWhateverTheirOrder(CellSymbol first, CellSymbol second)
    {
        var carryIn = PeriodCarryIn.Of([Before(FirstRowId, 1, first), Before(FirstRowId, 1, second)], FirstDay)[FirstRowId];

        carryIn.Days.ShouldBe(1);
        carryIn.IsMixed.ShouldBeTrue();
        carryIn.LastSymbol.ShouldBe(CellSymbol.Early);
    }

    private static BitmapAssignment[] EarlyDaysBefore(string agentId, int days)
        => Enumerable.Range(1, days).Select(d => Before(agentId, d, CellSymbol.Early)).ToArray();

    private static BitmapAssignment Before(string agentId, int daysBefore, CellSymbol symbol)
        => new(agentId, FirstDay.AddDays(-daysBefore), symbol, Guid.NewGuid(), [Guid.NewGuid()], false, Hours: ShiftHours);

    private static string? Diagnose(BundleProtection protection, HarmonyBitmap bitmap, PlanCellSwap swap)
    {
        var before = protection.Capture(bitmap, [swap]);
        PlanMutationValidator.Apply(bitmap, swap);

        return protection.Diagnose(bitmap, before);
    }

    private static HarmonyBitmap Build(params string[] rowPatterns)
    {
        var dayCount = rowPatterns[0].Length;
        var rows = rowPatterns
            .Select((_, i) => new BitmapAgent($"a{i}", $"Agent {i}", 0m, new HashSet<CellSymbol>()))
            .ToList();
        var days = Enumerable.Range(0, dayCount).Select(d => FirstDay.AddDays(d)).ToList();
        var cells = new Cell[rows.Count, dayCount];
        for (var r = 0; r < rows.Count; r++)
        {
            for (var d = 0; d < dayCount; d++)
            {
                cells[r, d] = CellOf(rowPatterns[r][d]);
            }
        }

        return new HarmonyBitmap(rows, days, cells);
    }

    private static Cell CellOf(char symbol) => symbol switch
    {
        'E' => Worked(CellSymbol.Early),
        'L' => Worked(CellSymbol.Late),
        'N' => Worked(CellSymbol.Night),
        _ => Cell.Free(),
    };

    private static Cell Worked(CellSymbol symbol)
        => new(symbol, Guid.NewGuid(), [Guid.NewGuid()], false, Hours: ShiftHours);
}
