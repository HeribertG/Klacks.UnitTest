// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization of the part resources ShiftTimeCutPlanner builds through ShiftCutPartResourceFactory. Intentional
/// (matches the Ui clone before it changes the time dimension): every part carries the plannable shift's briefing, debriefing
/// and travel fields (BeforeShift, AfterShift, BriefingTime, DebriefingTime, TravelTimeBefore, TravelTimeAfter) next to
/// customer, macro, dates, weekdays, flags, staff count, quantity and groups; only name, abbreviation, start, end, work time
/// and the midnight flag come from the cut part. Before the factory the time cut left those six fields at their defaults.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ShiftTimeCutPlannerPartResourceTests
{
    private static readonly Guid SealedOrderId = Guid.NewGuid();
    private static readonly Guid OriginalShiftId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid MacroId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();

    private ShiftTimeCutPlanner _planner = null!;

    [SetUp]
    public void SetUp()
    {
        var shiftRepository = Substitute.For<IShiftRepository>();
        shiftRepository.Get(OriginalShiftId).Returns(new Shift
        {
            Id = OriginalShiftId,
            Name = "24h Dienst",
            Description = "Notiz",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            OriginalId = SealedOrderId,
            ClientId = ClientId,
            MacroId = MacroId,
            StartShift = new TimeOnly(7, 0),
            EndShift = new TimeOnly(7, 0),
            BeforeShift = new TimeOnly(0, 15),
            AfterShift = new TimeOnly(0, 20),
            BriefingTime = new TimeOnly(0, 10),
            DebriefingTime = new TimeOnly(0, 5),
            TravelTimeBefore = new TimeOnly(0, 30),
            TravelTimeAfter = new TimeOnly(0, 25),
            FromDate = new DateOnly(2026, 1, 1),
            SumEmployees = 2,
            Quantity = 3,
            IsMonday = true,
            IsWednesday = true,
            IsTimeRange = true,
            IsSporadic = true
        });
        shiftRepository.GetGroupsForShift(OriginalShiftId).Returns(new List<Group> { new() { Id = GroupId } });
        var expensesRepository = Substitute.For<IShiftExpensesRepository>();
        expensesRepository.GetByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ShiftExpenses>());
        _planner = new ShiftTimeCutPlanner(
            shiftRepository, Substitute.For<IShiftReader>(), ShiftVisibilityGuards.AllowAll(), expensesRepository);
    }

    [Test]
    public async Task EveryPart_CarriesTheBriefingDebriefingAndTravelFieldsOfThePlannableShift()
    {
        var plan = await _planner.PlanAsync(OriginalShiftId, "07:00-15:00,15:00-23:00,23:00-07:00", "Early,Late,Night", "en");

        plan.IsRefused.ShouldBeFalse(plan.Refusal);
        var parts = plan.Operations.Select(operation => operation.Data).ToList();
        parts.Count.ShouldBe(3);
        parts.ShouldAllBe(part =>
            part.BeforeShift == new TimeOnly(0, 15)
            && part.AfterShift == new TimeOnly(0, 20)
            && part.BriefingTime == new TimeOnly(0, 10)
            && part.DebriefingTime == new TimeOnly(0, 5)
            && part.TravelTimeBefore == new TimeOnly(0, 30)
            && part.TravelTimeAfter == new TimeOnly(0, 25));
    }

    [Test]
    public async Task EveryPart_KeepsTheOrderDimensions_AndTakesOnlyNameAndTimesFromTheCut()
    {
        var plan = await _planner.PlanAsync(OriginalShiftId, "07:00-15:00,15:00-23:00,23:00-07:00", "Early,Late,Night", "en");

        var parts = plan.Operations.Select(operation => operation.Data).ToList();
        parts.Select(part => part.Name).ShouldBe(["Early", "Late", "Night"]);
        parts.Select(part => part.StartShift).ShouldBe([new TimeOnly(7, 0), new TimeOnly(15, 0), new TimeOnly(23, 0)]);
        parts.Select(part => part.EndShift).ShouldBe([new TimeOnly(15, 0), new TimeOnly(23, 0), new TimeOnly(7, 0)]);
        parts.ShouldAllBe(part =>
            part.Status == ShiftStatus.SplitShift
            && part.OriginalId == SealedOrderId
            && part.ClientId == ClientId
            && part.MacroId == MacroId
            && part.Description == "Notiz"
            && part.FromDate == new DateOnly(2026, 1, 1)
            && part.SumEmployees == 2
            && part.Quantity == 3
            && part.IsMonday
            && part.IsWednesday
            && !part.IsTuesday
            && part.IsTimeRange
            && part.IsSporadic
            && part.Groups.Count == 1
            && part.Groups[0].Id == GroupId);
    }
}
