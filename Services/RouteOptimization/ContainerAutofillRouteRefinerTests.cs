// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Direct tests for ContainerAutofillRouteRefiner: the post-ACO TimeRange validation and the post-insertion.
/// Through the service these paths are only reachable with more than two stops, where the unseeded ant colony
/// optimizer makes the order nondeterministic, so they are pinned here on hand-built route contexts.
/// The matrix is a 1D line (position = latitude, 1 unit = 1 km = 600 s); the base sits at position 0.
/// </summary>

using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
public class ContainerAutofillRouteRefinerTests
{
    private const double SecondsPerUnit = 600;
    private const double ContainerFromSeconds = 8 * 3600;
    private const double ContainerEndSeconds = 12 * 3600;
    private const double LargeBudgetSeconds = 4 * 3600;

    private static readonly TimeOnly WideStart = new(8, 0);
    private static readonly TimeOnly WideEnd = new(12, 0);
    private static readonly TimeOnly LateStart = new(14, 0);
    private static readonly TimeOnly LateEnd = new(15, 0);

    private ContainerAutofillRouteRefiner _refiner = null!;

    [SetUp]
    public void SetUp() => _refiner = new ContainerAutofillRouteRefiner(NullLogger<ContainerAutofillRouteRefiner>.Instance);

    [Test]
    public void Validate_RemovesLastStopOutsideItsTimeRange_AndLeavesInputUntouched()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd), Stop(2, LateStart, LateEnd)]);
        var route = new List<int> { 0, 1 };

        // Act
        var result = _refiner.ValidateRouteTimeRanges(context, route);

        // Assert
        result.ShouldBe([0]);
        route.ShouldBe([0, 1]);
    }

    [Test]
    public void Validate_KeepsLockedViolator_AndRemovesUnlockedViolatorBeforeIt()
    {
        // Arrange
        var context = CreateContext([Stop(1, LateStart, LateEnd), Stop(2, LateStart, LateEnd)]);

        // Act
        var result = _refiner.ValidateRouteTimeRanges(context, [0, 1], [1]);

        // Assert
        result.ShouldBe([1]);
    }

    [Test]
    public void Validate_ChecksContainerEndOnlyForTheLastStop()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd), Stop(2, WideStart, WideEnd)], containerEndSeconds: 9 * 3600);

        // Act
        var result = _refiner.ValidateRouteTimeRanges(context, [0, 1]);

        // Assert
        result.ShouldBe([0]);
    }

    [Test]
    public void Validate_EmptyRoute_StaysEmpty()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd)]);

        // Act
        var result = _refiner.ValidateRouteTimeRanges(context, []);

        // Assert
        result.ShouldBeEmpty();
    }

    [Test]
    public void Insert_NoRemainingCandidates_ReturnsSameRouteInstance()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd)]);
        var route = new List<int> { 0 };

        // Act
        var result = _refiner.PostOptimizationInsertion(context, route, []);

        // Assert
        result.ShouldBeSameAs(route);
    }

    [Test]
    public void Insert_EqualCostPositions_TakesTheFirst_AndConsumesTheCandidate()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd), Stop(2, WideStart, WideEnd)]);
        var remaining = new List<int> { 1 };

        // Act
        var result = _refiner.PostOptimizationInsertion(context, [0], remaining);

        // Assert
        result.ShouldBe([1, 0]);
        remaining.ShouldBeEmpty();
    }

    [Test]
    public void Insert_OverBudget_IsRejected()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd), Stop(2, WideStart, WideEnd)], budgetSeconds: 5999);
        var remaining = new List<int> { 1 };

        // Act
        var result = _refiner.PostOptimizationInsertion(context, [0], remaining);

        // Assert
        result.ShouldBe([0]);
        remaining.ShouldBe([1]);
    }

    [Test]
    public void Insert_ReturnAfterContainerEnd_IsRejected()
    {
        // Arrange
        var context = CreateContext([Stop(1, WideStart, WideEnd), Stop(2, WideStart, WideEnd)], containerEndSeconds: ContainerFromSeconds + 5999);

        // Act
        var result = _refiner.PostOptimizationInsertion(context, [0], [1]);

        // Assert
        result.ShouldBe([0]);
    }

    [Test]
    public void Insert_PositionThatPushesAFollowingStopOutOfItsTimeRange_IsSkipped()
    {
        // Arrange
        var context = CreateContext([Stop(1, new TimeOnly(8, 0), new TimeOnly(8, 20)), Stop(2, WideStart, WideEnd)]);

        // Act
        var result = _refiner.PostOptimizationInsertion(context, [0], [1]);

        // Assert
        result.ShouldBe([0, 1]);
    }

    private static Location Stop(double position, TimeOnly windowStart, TimeOnly windowEnd) => new()
    {
        Name = $"Stop{position}",
        Latitude = position,
        ShiftId = Guid.NewGuid(),
        WorkTime = TimeSpan.FromMinutes(30),
        TimeRangeStart = windowStart,
        TimeRangeEnd = windowEnd
    };

    private static ContainerAutofillRouteContext CreateContext(
        List<Location> stops,
        double containerEndSeconds = ContainerEndSeconds,
        double budgetSeconds = LargeBudgetSeconds)
    {
        var locations = new List<Location>(stops) { new() { Name = "Base", Latitude = 0, ShiftId = Guid.Empty } };
        var size = locations.Count;
        var distances = new double[size, size];
        var durations = new double[size, size];
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                var units = Math.Abs(locations[i].Latitude - locations[j].Latitude);
                distances[i, j] = units;
                durations[i, j] = units * SecondsPerUnit;
            }
        }

        var baseIndex = size - 1;
        return new ContainerAutofillRouteContext(
            new DistanceMatrix(locations, distances, durations),
            baseIndex, baseIndex, ContainerFromSeconds, containerEndSeconds, budgetSeconds, 0.5, [], []);
    }
}
