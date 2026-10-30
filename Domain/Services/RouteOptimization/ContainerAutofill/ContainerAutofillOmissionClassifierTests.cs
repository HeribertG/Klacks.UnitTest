// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization.ContainerAutofill;

[TestFixture]
public class ContainerAutofillOmissionClassifierTests
{
    private const int HourSeconds = 3600;
    private const double DayStartSeconds = 8.0 * HourSeconds;
    private const double DayEndSeconds = 12.0 * HourSeconds;
    private const double TravelSeconds = 600;

    [Test]
    public void StopThatDoesNotFitEvenAlone_IsTooLongForTheDay()
    {
        var omission = ClassifyLoneCandidate(onSiteHours: 5, windowStart: null, windowEnd: null);

        omission.Reason.ShouldBe(ContainerAutofillOmissionReason.TooLongForTheDay);
    }

    [Test]
    public void WindowNoArrivalHits_IsATimeWindowConflict()
    {
        var omission = ClassifyLoneCandidate(onSiteHours: 1, new TimeOnly(11, 0), new TimeOnly(11, 30));

        omission.Reason.ShouldBe(ContainerAutofillOmissionReason.TimeWindowConflict);
    }

    [Test]
    public void WindowAnArrivalHits_MeansTheTourHasNoRoom()
    {
        var omission = ClassifyLoneCandidate(onSiteHours: 1, new TimeOnly(8, 0), new TimeOnly(9, 0));

        omission.Reason.ShouldBe(ContainerAutofillOmissionReason.NoRoomOnTour);
    }

    [Test]
    public void ArrivalAfterAStopOnTheTour_CountsAsHit()
    {
        var onTour = Stop(hours: 2, null, null);
        var left = Stop(hours: 1, new TimeOnly(10, 0), new TimeOnly(10, 30));
        var context = Context([onTour, left]);

        var omission = ContainerAutofillOmissionClassifier.Classify(context, [0], [onTour, left]).Single();

        omission.ShiftId.ShouldBe(left.ShiftId);
        omission.Reason.ShouldBe(ContainerAutofillOmissionReason.NoRoomOnTour);
    }

    [Test]
    public void StopsOnTheRoute_AreNotReported()
    {
        var onTour = Stop(hours: 1, null, null);

        ContainerAutofillOmissionClassifier.Classify(Context([onTour]), [0], [onTour]).ShouldBeEmpty();
    }

    private static ContainerAutofillOmission ClassifyLoneCandidate(double onSiteHours, TimeOnly? windowStart, TimeOnly? windowEnd)
    {
        var candidate = Stop(onSiteHours, windowStart, windowEnd);

        return ContainerAutofillOmissionClassifier.Classify(Context([candidate]), [], [candidate]).Single();
    }

    private static Location Stop(double hours, TimeOnly? windowStart, TimeOnly? windowEnd) => new()
    {
        ShiftId = Guid.NewGuid(),
        WorkTime = TimeSpan.FromHours(hours),
        TimeRangeStart = windowStart,
        TimeRangeEnd = windowEnd,
    };

    private static ContainerAutofillRouteContext Context(List<Location> candidates)
    {
        var locations = new List<Location>(candidates) { new() };
        var size = locations.Count;
        var durations = new double[size, size];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                durations[i, j] = i == j ? 0 : TravelSeconds;
            }
        }

        return new ContainerAutofillRouteContext(
            new DistanceMatrix(locations, new double[size, size], durations),
            size - 1, size - 1, DayStartSeconds, DayEndSeconds, DayEndSeconds - DayStartSeconds, 1, [], []);
    }
}
