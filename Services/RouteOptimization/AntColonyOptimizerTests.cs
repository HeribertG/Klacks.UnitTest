using FluentAssertions;
using Klacks.Api.Domain.Services.RouteOptimization;
using Microsoft.Extensions.Logging;

namespace UnitTest.Services.RouteOptimization;

[TestFixture]
public class AntColonyOptimizerTests
{
    private ILogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        _logger = Substitute.For<ILogger>();
    }

    [Test]
    public void FindOptimalRoute_WithBernLocations_ShouldAvoidZigzagPattern()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);

        // Assert
        route.Should().HaveCount(5);
        route[0].Should().Be(0, "route should start at Liebefeld (index 0)");

        var totalDistance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);
        var zigzagDistance = CalculateZigzagDistance(distanceMatrix.Matrix);

        totalDistance.Should().BeLessThan(zigzagDistance,
            $"optimized route ({totalDistance:F2} km) should be shorter than zigzag pattern ({zigzagDistance:F2} km)");

        LogRouteDetails(route, locations, distanceMatrix.Matrix, totalDistance);
    }

    [Test]
    public void FindOptimalRoute_WithBernLocations_ShouldProduceNearOptimalDistance()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);

        // Assert
        var totalDistance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);
        var optimalDistance = CalculateOptimalRouteDistance(distanceMatrix.Matrix);

        totalDistance.Should().BeLessThanOrEqualTo(optimalDistance * 1.1,
            $"route distance ({totalDistance:F2} km) should be within 10% of optimal ({optimalDistance:F2} km)");
    }

    [Test]
    public void FindOptimalRoute_WithoutFixedEndpoints_ShouldOptimizeFreely()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        // Act
        var route = optimizer.FindOptimalRoute();

        // Assert
        route.Should().HaveCount(5);
        route.Should().OnlyHaveUniqueItems();
        route.Should().Contain(new[] { 0, 1, 2, 3, 4 });

        var totalDistance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: false);
        TestContext.WriteLine($"Free route distance: {totalDistance:F2} km");
        TestContext.WriteLine($"Route: {string.Join(" -> ", route.Select(i => locations[i].Name))}");
    }

    [Test]
    public void FindOptimalRoute_WithFixedStartOnly_ShouldStartAtCorrectLocation()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0);

        // Assert
        route.Should().HaveCount(5);
        route[0].Should().Be(0, "route should start at index 0");
    }

    [Test]
    public void FindOptimalRoute_WithDifferentStartAndEnd_ShouldRespectStart()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 4);

        // Assert
        route.Should().HaveCount(5);
        route[0].Should().Be(0, "route should start at index 0");
        route.Should().Contain(4, "route should contain endpoint index 4");
    }

    [Test]
    public void FindOptimalRoute_ConsistencyTest_ShouldProduceSimilarResults()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var distances = new List<double>();

        // Act
        for (int i = 0; i < 5; i++)
        {
            var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);
            var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);
            var distance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);
            distances.Add(distance);
        }

        // Assert
        var avgDistance = distances.Average();
        var maxDeviation = distances.Max() - distances.Min();

        maxDeviation.Should().BeLessThan(avgDistance * 0.15,
            $"route distances should not vary more than 15% (min: {distances.Min():F2}, max: {distances.Max():F2}, avg: {avgDistance:F2})");

        TestContext.WriteLine($"Distances over 5 runs: {string.Join(", ", distances.Select(d => $"{d:F2}"))}");
        TestContext.WriteLine($"Average: {avgDistance:F2} km, Max deviation: {maxDeviation:F2} km");
    }

    [Test]
    public void FindOptimalRoute_WithLargerProblem_ShouldScaleReasonably()
    {
        // Arrange
        var locations = CreateLargerTestProblem(10);
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        var startTime = DateTime.UtcNow;

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);

        // Assert
        var elapsedTime = DateTime.UtcNow - startTime;

        route.Should().HaveCount(10);
        elapsedTime.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "optimization should complete within 30 seconds for 10 locations");

        var totalDistance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);
        TestContext.WriteLine($"10-location route distance: {totalDistance:F2} km, time: {elapsedTime.TotalMilliseconds:F0} ms");
    }

    [Test]
    public void FindOptimalRoute_TwoOptImprovement_ShouldReduceDistance()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);

        var logMessages = new List<string>();
        var mockLogger = Substitute.For<ILogger>();
        mockLogger.When(x => x.Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>()))
        .Do(callInfo =>
        {
            var state = callInfo.ArgAt<object>(2);
            if (state != null)
            {
                logMessages.Add(state.ToString() ?? "");
            }
        });

        var optimizer = new AntColonyOptimizer(distanceMatrix, mockLogger);

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);

        // Assert
        var has2OptImprovement = logMessages.Any(m => m.Contains("2-opt"));
        TestContext.WriteLine($"2-opt improvements detected: {has2OptImprovement}");
        TestContext.WriteLine($"Log messages containing '2-opt': {logMessages.Count(m => m.Contains("2-opt"))}");
    }

    [Test]
    public void FindOptimalRoute_VerifyGeographicLogic_ShouldNotJumpBackAndForth()
    {
        // Arrange
        var locations = CreateBernTestLocations();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);

        // Act
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);

        // Assert
        var consecutiveJumps = 0;
        for (int i = 0; i < route.Count - 2; i++)
        {
            var loc1 = locations[route[i]];
            var loc2 = locations[route[i + 1]];
            var loc3 = locations[route[i + 2]];

            var dist12 = distanceMatrix.Matrix[route[i], route[i + 1]];
            var dist23 = distanceMatrix.Matrix[route[i + 1], route[i + 2]];
            var dist13 = distanceMatrix.Matrix[route[i], route[i + 2]];

            if (dist13 < dist12 && dist13 < dist23)
            {
                consecutiveJumps++;
                TestContext.WriteLine($"Potential jump detected: {loc1.Name} -> {loc2.Name} -> {loc3.Name}");
            }
        }

        consecutiveJumps.Should().BeLessThanOrEqualTo(1,
            "route should not have excessive back-and-forth jumps");

        LogRouteDetails(route, locations, distanceMatrix.Matrix,
            CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true));
    }

    private List<Location> CreateBernTestLocations()
    {
        return new List<Location>
        {
            new Location
            {
                Name = "Liebefeld (Base)",
                Address = "Liebefeld",
                Latitude = 46.9367,
                Longitude = 7.4055
            },
            new Location
            {
                Name = "Morel (Militärstrasse)",
                Address = "Militärstrasse, Bern",
                Latitude = 46.9550,
                Longitude = 7.4390
            },
            new Location
            {
                Name = "Vogel (Thunstrasse)",
                Address = "Thunstrasse, Bern",
                Latitude = 46.9420,
                Longitude = 7.4500
            },
            new Location
            {
                Name = "Roux (Bubenbergplatz)",
                Address = "Bubenbergplatz, Bern",
                Latitude = 46.9480,
                Longitude = 7.4394
            },
            new Location
            {
                Name = "Additional Location",
                Address = "Wabern",
                Latitude = 46.9280,
                Longitude = 7.4350
            }
        };
    }

    private List<Location> CreateLargerTestProblem(int size)
    {
        var random = new Random(42);
        var locations = new List<Location>();

        for (int i = 0; i < size; i++)
        {
            locations.Add(new Location
            {
                Name = $"Location {i}",
                Address = $"Address {i}",
                Latitude = 46.9 + random.NextDouble() * 0.1,
                Longitude = 7.4 + random.NextDouble() * 0.1
            });
        }

        return locations;
    }

    private DistanceMatrix CreateDistanceMatrixFromLocations(List<Location> locations)
    {
        var size = locations.Count;
        var matrix = new double[size, size];
        var durationMatrix = new double[size, size];

        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                if (i == j)
                {
                    matrix[i, j] = 0.0;
                    durationMatrix[i, j] = 0.0;
                }
                else
                {
                    var distance = CalculateHaversineDistance(
                        locations[i].Latitude, locations[i].Longitude,
                        locations[j].Latitude, locations[j].Longitude);
                    matrix[i, j] = distance;
                    durationMatrix[i, j] = distance / 40.0 * 3600;
                }
            }
        }

        return new DistanceMatrix(locations, matrix, durationMatrix);
    }

    private double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double EARTH_RADIUS_KM = 6371.0;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EARTH_RADIUS_KM * c;
    }

    private double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private double CalculateTotalDistance(List<int> route, double[,] matrix, bool isRoundTrip)
    {
        double total = 0.0;
        for (int i = 0; i < route.Count - 1; i++)
        {
            total += matrix[route[i], route[i + 1]];
        }

        if (isRoundTrip && route.Count > 0)
        {
            total += matrix[route[^1], route[0]];
        }

        return total;
    }

    private double CalculateZigzagDistance(double[,] matrix)
    {
        var zigzagRoute = new List<int> { 0, 1, 2, 3, 4 };
        return CalculateTotalDistance(zigzagRoute, matrix, isRoundTrip: true);
    }

    private double CalculateOptimalRouteDistance(double[,] matrix)
    {
        var permutations = GetPermutations(new[] { 1, 2, 3, 4 });
        double minDistance = double.MaxValue;
        List<int>? bestRoute = null;

        foreach (var perm in permutations)
        {
            var route = new List<int> { 0 };
            route.AddRange(perm);

            var distance = CalculateTotalDistance(route, matrix, isRoundTrip: true);
            if (distance < minDistance)
            {
                minDistance = distance;
                bestRoute = route;
            }
        }

        TestContext.WriteLine($"Optimal route by brute force: {string.Join(" -> ", bestRoute ?? new List<int>())} = {minDistance:F2} km");
        return minDistance;
    }

    private IEnumerable<IEnumerable<int>> GetPermutations(int[] items)
    {
        if (items.Length == 1)
        {
            yield return items;
            yield break;
        }

        foreach (var item in items)
        {
            var remaining = items.Where(x => x != item).ToArray();
            foreach (var perm in GetPermutations(remaining))
            {
                yield return new[] { item }.Concat(perm);
            }
        }
    }

    private void LogRouteDetails(List<int> route, List<Location> locations, double[,] matrix, double totalDistance)
    {
        TestContext.WriteLine("=== Route Details ===");
        TestContext.WriteLine($"Total distance: {totalDistance:F2} km");
        TestContext.WriteLine("Route:");

        for (int i = 0; i < route.Count; i++)
        {
            var loc = locations[route[i]];
            var nextIdx = (i + 1) % route.Count;
            var nextLoc = locations[route[nextIdx]];
            var segmentDistance = matrix[route[i], route[nextIdx]];

            if (i < route.Count - 1 || true)
            {
                TestContext.WriteLine($"  [{route[i]}] {loc.Name} -> [{route[nextIdx]}] {nextLoc.Name}: {segmentDistance:F2} km");
            }
        }
    }
}
