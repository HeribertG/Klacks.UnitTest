// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ContainerVisitPlacement: the write rules shared by optimize_container_route and the bundle skill - applying a placed
/// visit to its item, the 24-hour travel refusal, the verification of the re-read template and the stop list.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Services.RouteOptimization.VisitScheduling;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ContainerVisitPlacementTests
{
    private static readonly Guid ShiftA = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid ShiftB = Guid.Parse("e0000000-0000-0000-0000-000000000002");

    private static VisitItem Item(Guid shiftId) =>
        new(shiftId, true, 0.75, TimeOnly.MinValue, TimeOnly.MinValue, TransportMode.ByCar);

    private static ContainerTemplateItemResource TemplateItem(Guid shiftId, int startMinutes) => new()
    {
        ShiftId = shiftId,
        TimeRangeStartItem = ContainerVisitScheduler.ClockTime(startMinutes),
        TimeRangeEndItem = ContainerVisitScheduler.ClockTime(startMinutes + 45),
        TravelTimeBefore = new TimeOnly(0, 10),
        TravelTimeAfter = new TimeOnly(0, 5)
    };

    [Test]
    public void ApplyTimes_WritesWindowAndTravel_AndKeepsTheStoredTravelAfterWhenNoneIsPlaced()
    {
        var item = new ContainerTemplateItemResource { ShiftId = ShiftA, TravelTimeAfter = new TimeOnly(0, 7) };

        ContainerVisitPlacement.ApplyTimes(new ScheduledVisit(Item(ShiftA), 491, 536, 10, null), item);

        item.TimeRangeStartItem.ShouldBe(new TimeOnly(8, 11));
        item.TimeRangeEndItem.ShouldBe(new TimeOnly(8, 56));
        item.TravelTimeBefore.ShouldBe(new TimeOnly(0, 10));
        item.TravelTimeAfter.ShouldBe(new TimeOnly(0, 7));

        ContainerVisitPlacement.ApplyTimes(new ScheduledVisit(Item(ShiftA), 491, 536, 10, 12), item);

        item.TravelTimeAfter.ShouldBe(new TimeOnly(0, 12));
    }

    [TestCase(1439, null, false)]
    [TestCase(1440, null, true)]
    [TestCase(10, 1440, true)]
    public void HasTravelOfADayOrMore(int travelBefore, int? travelAfter, bool expected) =>
        ContainerVisitPlacement.HasTravelOfADayOrMore([new ScheduledVisit(Item(ShiftA), 0, 45, travelBefore, travelAfter)])
            .ShouldBe(expected);

    [Test]
    public void StoredVisitTimesMatch_RequiresRouteInfoAndEveryWrittenTime()
    {
        var written = new ContainerTemplateResource
        {
            ContainerTemplateItems = [TemplateItem(ShiftA, 491), TemplateItem(ShiftB, 550)]
        };

        ContainerVisitPlacement.StoredVisitTimesMatch(written, Stored()).ShouldBeTrue();
        ContainerVisitPlacement.StoredVisitTimesMatch(written, Stored(s => s.RouteInfo = null)).ShouldBeFalse();
        ContainerVisitPlacement.StoredVisitTimesMatch(
            written, Stored(s => s.ContainerTemplateItems[0].TimeRangeStartItem = new TimeOnly(9, 11))).ShouldBeFalse();
        ContainerVisitPlacement.StoredVisitTimesMatch(
            written, Stored(s => s.ContainerTemplateItems[1].TravelTimeAfter = new TimeOnly(0, 59))).ShouldBeFalse();
        ContainerVisitPlacement.StoredVisitTimesMatch(
            written, Stored(s => s.ContainerTemplateItems.RemoveAt(0))).ShouldBeFalse();
    }

    [Test]
    public void BuildStops_NumbersTheVisitsInOrder_WithNameAndAddressOfTheirStep()
    {
        var steps = new List<RouteStep>
        {
            new(1, "Base", "Base", 0, 0, Guid.Empty, TransportMode.ByCar, 1, TimeSpan.FromMinutes(10)),
            new(2, "Client A", "Street A", 0, 0, ShiftA, TransportMode.ByCar, 1, TimeSpan.FromMinutes(10)),
            new(3, "Client B", "Street B", 0, 0, ShiftB, TransportMode.ByCar, 1, TimeSpan.Zero)
        };

        var stops = ContainerVisitPlacement.BuildStops(
            [new ScheduledVisit(Item(ShiftA), 491, 536, 10, null), new ScheduledVisit(Item(ShiftB), 550, 595, 10, 10)],
            steps);

        stops.ShouldBe(
        [
            new ContainerRouteStop(1, ShiftA, "Client A", "Street A", "08:11", "08:56", "00:10"),
            new ContainerRouteStop(2, ShiftB, "Client B", "Street B", "09:10", "09:55", "00:10")
        ]);
    }

    private static ContainerTemplateResource Stored(Action<ContainerTemplateResource>? change = null)
    {
        var stored = new ContainerTemplateResource
        {
            RouteInfo = new RouteInfoResource(),
            ContainerTemplateItems = [TemplateItem(ShiftB, 550), TemplateItem(ShiftA, 491)]
        };
        change?.Invoke(stored);
        return stored;
    }
}
