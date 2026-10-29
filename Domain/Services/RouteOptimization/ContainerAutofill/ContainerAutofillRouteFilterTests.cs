// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ContainerAutofillRouteFilter: dropped visits leave the route, the kept ones keep their order, every leg and total is
/// read again from the matrices (no figure still runs through a dropped stop), the budget moves by the saved time, and
/// the directions of the old route are dropped. Nothing kept gives the empty result.
/// </summary>

using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;
using Klacks.Api.Domain.Services.RouteOptimization.VisitScheduling;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization.ContainerAutofill;

[TestFixture]
public class ContainerAutofillRouteFilterTests
{
    private static readonly Guid Visit1 = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Visit2 = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid Visit3 = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    [Test]
    public void DroppedMiddleVisit_LeavesTheRoute_AndTheLegSkipsIt()
    {
        var raw = ContainerAutofillRouteFixture.Route([Visit1, Visit2, Visit3]) with
        {
            SegmentDirections = [new Klacks.Api.Domain.Services.RouteOptimization.RouteSegmentDirections("Client 1", "Client 2", "driving", 1, TimeSpan.FromMinutes(1), [])]
        };

        var kept = ContainerAutofillRouteFilter.KeepShifts(raw, new HashSet<Guid> { Visit1, Visit3 });

        kept.OptimizedRoute.Select(l => l.ShiftId).ShouldBe([Guid.Empty, Visit1, Visit3, Guid.Empty]);
        kept.OptimizedRoute.ShouldNotContain(l => l.Name == ContainerAutofillRouteFixture.VisitName(2));
        kept.FullRouteIndices.ShouldBe([0, 1, 3, 4]);
        kept.RouteIndices.ShouldBe([1, 3]);
        kept.SelectedShiftIds.ShouldBe([Visit1, Visit3]);
        kept.SelectedShiftCount.ShouldBe(2);
        kept.SegmentDirections.ShouldBeNull();
        kept.PlacedTimeBlocks.ShouldBeNull();

        var steps = RouteStepBuilder.FromAutofill(kept);
        steps[1].TravelTimeToNext.ShouldBe(TimeSpan.FromSeconds(1.5 * ContainerAutofillRouteFixture.LegSeconds));
        steps[1].DistanceToNextKm.ShouldBe(1.5 * ContainerAutofillRouteFixture.LegKm);
    }

    [Test]
    public void Totals_AreRecomputedFromTheKeptRoute()
    {
        var raw = ContainerAutofillRouteFixture.Route([Visit1, Visit2, Visit3]);

        var kept = ContainerAutofillRouteFilter.KeepShifts(raw, new HashSet<Guid> { Visit1, Visit3 });

        var legSeconds = ContainerAutofillRouteFixture.LegSeconds;
        var onSite = TimeSpan.FromMinutes(2 * ContainerAutofillRouteFixture.OnSiteMinutes);
        var estimated = TimeSpan.FromSeconds(3.5 * legSeconds) + onSite;
        kept.TotalDistanceKm.ShouldBe(3.5 * ContainerAutofillRouteFixture.LegKm);
        kept.TotalDistanceKm.ShouldBeLessThan(raw.TotalDistanceKm);
        kept.TotalWorkTime.ShouldBe(onSite);
        kept.TotalBriefingDebriefingTime.ShouldBe(onSite);
        kept.EstimatedTravelTime.ShouldBe(estimated);
        kept.RemainingTime.ShouldBe(TimeSpan.FromHours(ContainerAutofillRouteFixture.BudgetHours) - estimated);
        kept.TravelTimeFromStartBase.ShouldBe(TimeSpan.FromSeconds(legSeconds));
        kept.DistanceFromStartBaseKm.ShouldBe(ContainerAutofillRouteFixture.LegKm);
        kept.TravelTimeToEndBase.ShouldBe(TimeSpan.FromSeconds(legSeconds));
        kept.DistanceToEndBaseKm.ShouldBe(ContainerAutofillRouteFixture.LegKm);
    }

    [Test]
    public void DroppedFirstAndLastVisit_RecomputesTheBaseLegs()
    {
        var raw = ContainerAutofillRouteFixture.Route([Visit1, Visit2, Visit3]);

        var kept = ContainerAutofillRouteFilter.KeepShifts(raw, new HashSet<Guid> { Visit2 });

        kept.FullRouteIndices.ShouldBe([0, 2, 4]);
        kept.TravelTimeFromStartBase.ShouldBe(TimeSpan.FromSeconds(1.5 * ContainerAutofillRouteFixture.LegSeconds));
        kept.DistanceFromStartBaseKm.ShouldBe(1.5 * ContainerAutofillRouteFixture.LegKm);
        kept.TravelTimeToEndBase.ShouldBe(TimeSpan.FromSeconds(1.5 * ContainerAutofillRouteFixture.LegSeconds));
        kept.DistanceToEndBaseKm.ShouldBe(1.5 * ContainerAutofillRouteFixture.LegKm);
        kept.TotalDistanceKm.ShouldBe(3 * ContainerAutofillRouteFixture.LegKm);
    }

    [Test]
    public void MisalignedRouteAndIndices_AreRefused()
    {
        var raw = ContainerAutofillRouteFixture.Route([Visit1, Visit2]);
        var misaligned = raw with { FullRouteIndices = raw.FullRouteIndices.SkipLast(1).ToList() };

        Should.Throw<ArgumentException>(() => ContainerAutofillRouteFilter.KeepShifts(misaligned, new HashSet<Guid> { Visit1 }));
    }

    [Test]
    public void NothingKept_IsTheEmptyResult_WithTheCandidateCount()
    {
        var raw = ContainerAutofillRouteFixture.Route([Visit1, Visit2], totalAvailableShifts: 5);

        var kept = ContainerAutofillRouteFilter.KeepShifts(raw, new HashSet<Guid>());

        kept.SelectedShiftIds.ShouldBeEmpty();
        kept.SelectedShiftCount.ShouldBe(0);
        kept.OptimizedRoute.ShouldBeEmpty();
        kept.TotalAvailableShifts.ShouldBe(5);
    }

    [Test]
    public void EverythingKept_LeavesTheRouteAndTotalsUnchanged()
    {
        var raw = ContainerAutofillRouteFixture.Route([Visit1, Visit2]);

        var kept = ContainerAutofillRouteFilter.KeepShifts(raw, new HashSet<Guid> { Visit1, Visit2 });

        kept.FullRouteIndices.ShouldBe(raw.FullRouteIndices);
        kept.TotalDistanceKm.ShouldBe(raw.TotalDistanceKm);
        kept.EstimatedTravelTime.ShouldBe(raw.EstimatedTravelTime);
        kept.RemainingTime.ShouldBe(raw.RemainingTime);
    }
}
