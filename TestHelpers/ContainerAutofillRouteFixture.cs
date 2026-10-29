// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A container autofill result with a real route as ContainerAutofillResultBuilder builds it: start base, the given
/// visits in order, end base. Location i sits at matrix index i; a leg between neighbours is LegKm and LegSeconds, a leg
/// that skips stops is shorter than the way through them: (gap + 1) / 2 legs for a gap of |i - j| (2 -> 1.5 legs,
/// 3 -> 2 legs), so a recomputation that keeps the old totals is caught. Every visit stays OnSiteMinutes on site and the
/// time budget is BudgetHours, so the totals are easy to recompute in a test.
/// </summary>

using Klacks.Api.Domain.Services.RouteOptimization;

namespace Klacks.UnitTest.TestHelpers;

public static class ContainerAutofillRouteFixture
{
    public const double LegKm = 2.5;
    public const int LegSeconds = 600;
    public const int OnSiteMinutes = 45;
    public const int BudgetHours = 10;
    public const string BaseName = "Bern";

    public static string VisitName(int position) => $"Client {position}";

    public static string VisitAddress(int position) => $"Street {position}, 3000 Bern";

    /// <summary>
    /// Autofill result over the given visits.
    /// </summary>
    /// <param name="visitIds">Task shift ids in route order</param>
    /// <param name="totalAvailableShifts">Candidates the autofill saw</param>
    /// <param name="isEstimated">True when the travel times are straight-line estimates</param>
    public static ContainerAutofillResult Route(
        IReadOnlyList<Guid> visitIds, int totalAvailableShifts = 3, bool isEstimated = false)
    {
        var size = visitIds.Count + 2;
        var locations = new List<Location> { Base() };
        locations.AddRange(visitIds.Select((id, index) => Visit(id, index + 1)));
        locations.Add(Base());

        var distance = new double[size, size];
        var duration = new double[size, size];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                distance[i, j] = LegFactor(i, j) * LegKm;
                duration[i, j] = LegFactor(i, j) * LegSeconds;
            }
        }

        var legs = visitIds.Count + 1;
        var onSite = TimeSpan.FromMinutes(OnSiteMinutes * visitIds.Count);
        var estimated = TimeSpan.FromSeconds(legs * LegSeconds) + onSite;
        var fullRouteIndices = Enumerable.Range(0, size).ToList();

        return new ContainerAutofillResult(
            OptimizedRoute: locations,
            SelectedShiftIds: visitIds.ToList(),
            TotalDistanceKm: legs * LegKm,
            EstimatedTravelTime: estimated,
            TotalWorkTime: onSite,
            RemainingTime: TimeSpan.FromHours(BudgetHours) - estimated,
            TotalAvailableShifts: totalAvailableShifts,
            SelectedShiftCount: visitIds.Count,
            DistanceMatrix: distance,
            DurationMatrix: duration,
            TravelTimeFromStartBase: TimeSpan.FromSeconds(LegSeconds),
            RouteIndices: fullRouteIndices.Skip(1).SkipLast(1).ToList(),
            DistanceFromStartBaseKm: LegKm,
            DistanceToEndBaseKm: LegKm,
            TravelTimeToEndBase: TimeSpan.FromSeconds(LegSeconds),
            FullRouteIndices: fullRouteIndices,
            TotalBriefingDebriefingTime: onSite,
            IsEstimated: isEstimated);
    }

    /// <summary>
    /// Length of the leg between index i and j in neighbour legs.
    /// </summary>
    public static double LegFactor(int i, int j) => i == j ? 0 : (Math.Abs(i - j) + 1) / 2d;

    private static Location Base() => new()
    {
        Name = BaseName,
        Address = BaseName,
        Latitude = 46.95,
        Longitude = 7.44,
        ShiftId = Guid.Empty
    };

    private static Location Visit(Guid shiftId, int position) => new()
    {
        Name = VisitName(position),
        Address = VisitAddress(position),
        Latitude = 46.95 + position / 100d,
        Longitude = 7.44 + position / 100d,
        ShiftId = shiftId,
        WorkTime = TimeSpan.FromMinutes(OnSiteMinutes)
    };
}
