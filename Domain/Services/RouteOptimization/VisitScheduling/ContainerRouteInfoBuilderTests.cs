// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ContainerRouteInfoBuilder from a container autofill result: the same RouteInfo the Ui stores after an autofill
/// (bases, totals and TimeSpans in their "c" wire text, one route location per step with the shift id as text).
/// </summary>

using Klacks.Api.Domain.Services.RouteOptimization.VisitScheduling;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization.VisitScheduling;

[TestFixture]
public class ContainerRouteInfoBuilderTests
{
    private static readonly Guid Visit1 = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Visit2 = Guid.Parse("d0000000-0000-0000-0000-000000000002");

    [Test]
    public void FromAutofill_CarriesTotalsBasesAndEveryStep()
    {
        var result = ContainerAutofillRouteFixture.Route([Visit1, Visit2]);
        var steps = RouteStepBuilder.FromAutofill(result);

        var routeInfo = ContainerRouteInfoBuilder.Build("Start", "End", result, steps);

        routeInfo.StartBase.ShouldBe("Start");
        routeInfo.EndBase.ShouldBe("End");
        routeInfo.TotalDistanceKm.ShouldBe(3 * ContainerAutofillRouteFixture.LegKm);
        routeInfo.EstimatedTravelTime.ShouldBe("02:00:00");
        routeInfo.TravelTimeFromStartBase.ShouldBe("00:10:00");
        routeInfo.TravelTimeToEndBase.ShouldBe("00:10:00");
        routeInfo.DistanceFromStartBaseKm.ShouldBe(ContainerAutofillRouteFixture.LegKm);
        routeInfo.DistanceToEndBaseKm.ShouldBe(ContainerAutofillRouteFixture.LegKm);
        routeInfo.SegmentDirections.ShouldBeNull();
        routeInfo.OptimizedRoute.Select(l => l.ShiftId)
            .ShouldBe([Guid.Empty.ToString(), Visit1.ToString(), Visit2.ToString(), Guid.Empty.ToString()]);
        routeInfo.OptimizedRoute[1].Name.ShouldBe(ContainerAutofillRouteFixture.VisitName(1));
        routeInfo.OptimizedRoute[1].TravelTimeToNext.ShouldBe("00:10:00");
        routeInfo.OptimizedRoute[1].Order.ShouldBe(2);
        routeInfo.OptimizedRoute[^1].TravelTimeToNext.ShouldBe("00:00:00");
    }
}
