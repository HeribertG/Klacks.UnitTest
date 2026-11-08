// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.Harmonizer.Evolution;
using Klacks.ScheduleOptimizer.Harmonizer.Scorer;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Search.Region;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search.Region;

/// <summary>
/// Placeholder rule of the Wizard 3 region rebuild: a rebuild that hands a stand-in a work a real person legally holds
/// is not admitted, also when the same rebuild frees an illegal work of another person; a work that breaks a rule where it
/// stands may go to the stand-in.
/// </summary>
[TestFixture]
public class RegionAdmissionStandInTests
{
    private const int RealRow = 0;
    private const int StandInRow = 1;
    private const int Day = 0;

    private static readonly DateOnly Date = new(2026, 12, 10);

    [Test]
    public void Admit_RealPersonsLegalWorkMovedToAStandIn_Refused()
    {
        var (original, rebuilt) = Plans();

        Admission(availability: null).Admit(original, rebuilt, [RealRow, StandInRow], Steps(), double.NegativeInfinity).ShouldBeNull();
    }

    [Test]
    public void Admit_RealPersonsIllegalWorkMovedToAStandIn_Admitted()
    {
        var (original, rebuilt) = Plans();
        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>
        {
            [("real", Date)] = new DayAvailability(WorksOnDay: false, HasFreeCommand: false, HasBreakBlocker: false),
        };

        Admission(availability).Admit(original, rebuilt, [RealRow, StandInRow], Steps(), double.NegativeInfinity).ShouldNotBeNull();
    }

    [Test]
    public void Admit_IllegalWorkToARealPersonAndALegalWorkToAStandIn_Refused()
    {
        var agents = new List<BitmapAgent>
        {
            new("illegal", "Illegal", 0m, new HashSet<CellSymbol>()),
            new("legal", "Legal", 0m, new HashSet<CellSymbol>()),
            new("receiver", "Receiver", 0m, new HashSet<CellSymbol>()),
            new("stand-in", "Stand-in", 0m, new HashSet<CellSymbol>(), IsStandIn: true),
        };
        var original = BitmapBuilder.Build(new BitmapInput(agents, Date, Date, []));
        var illegalWork = Work();
        var legalWork = Work();
        original.SetCell(0, Day, illegalWork);
        original.SetCell(1, Day, legalWork);
        var rebuilt = BitmapCloner.Clone(original);
        var freeOfReceiver = rebuilt.GetCell(2, Day);
        var freeOfStandIn = rebuilt.GetCell(3, Day);
        rebuilt.SetCell(2, Day, illegalWork);
        rebuilt.SetCell(0, Day, freeOfReceiver);
        rebuilt.SetCell(3, Day, legalWork);
        rebuilt.SetCell(1, Day, freeOfStandIn);
        var availability = new Dictionary<(string AgentId, DateOnly Date), DayAvailability>
        {
            [("illegal", Date)] = new DayAvailability(WorksOnDay: false, HasFreeCommand: false, HasBreakBlocker: false),
        };
        IReadOnlyList<PlanCellSwap> steps =
        [
            new PlanCellSwap(0, Day, 2, Day, string.Empty),
            new PlanCellSwap(1, Day, 3, Day, string.Empty),
        ];

        Admission(availability).Admit(original, rebuilt, [0, 1, 2, 3], steps, double.NegativeInfinity).ShouldBeNull();
    }

    private static Cell Work() => new(
        CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], false,
        Date.ToDateTime(new TimeOnly(8, 0)), Date.ToDateTime(new TimeOnly(16, 0)), 8m);

    private static RegionAdmission Admission(IReadOnlyDictionary<(string AgentId, DateOnly Date), DayAvailability>? availability)
        => new(
            new PlanMutationValidator(new DomainAwareReplaceValidator(availability)),
            new HarmonyFitnessEvaluator(new HarmonyScorer()),
            new TargetHoursDeviationGuard());

    private static IReadOnlyList<PlanCellSwap> Steps() => [new PlanCellSwap(RealRow, Day, StandInRow, Day, string.Empty)];

    private static (HarmonyBitmap Original, HarmonyBitmap Rebuilt) Plans()
    {
        var agents = new List<BitmapAgent>
        {
            new("real", "Real", 0m, new HashSet<CellSymbol>()),
            new("stand-in", "Stand-in", 0m, new HashSet<CellSymbol>(), IsStandIn: true),
        };
        var original = BitmapBuilder.Build(new BitmapInput(agents, Date, Date, []));
        var work = Work();
        original.SetCell(RealRow, Day, work);

        var rebuilt = BitmapCloner.Clone(original);
        var free = rebuilt.GetCell(StandInRow, Day);
        rebuilt.SetCell(StandInRow, Day, work);
        rebuilt.SetCell(RealRow, Day, free);
        return (original, rebuilt);
    }
}
