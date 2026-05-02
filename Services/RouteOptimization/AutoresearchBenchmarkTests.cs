// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Autoresearch-Benchmark fuer den ACO-Algorithmus und den Full-Autofill-Pipeline.
/// Misst Distanz-Optimalitaet, Konsistenz und Geschwindigkeit als einzelnen Score.
/// Ausgabe: AUTORESEARCH_SCORE (niedriger = besser, Ziel < 0.30).
/// </summary>

using Shouldly;
using Klacks.Api.Domain.Services.RouteOptimization;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
[Category("Autoresearch")]
public class AutoresearchBenchmarkTests
{
    private ILogger _logger = null!;

    private const int BENCHMARK_RUNS = 5;
    private const double WEIGHT_DISTANCE = 0.50;
    private const double WEIGHT_CONSISTENCY = 0.30;
    private const double WEIGHT_SPEED = 0.20;

    [SetUp]
    public void SetUp()
    {
        _logger = Substitute.For<ILogger>();
    }

    [Test]
    public void Benchmark_CompositeScore()
    {
        var scenarios = new List<(string Name, List<Location> Locations, double OptimalDistance)>
        {
            CreateSmallScenario(),
            CreateMediumScenario(),
            CreateClusteredScenario(),
            CreateLinearScenario()
        };

        var totalDistanceRatio = 0.0;
        var totalConsistency = 0.0;
        var totalSpeedPenalty = 0.0;
        var scenarioCount = scenarios.Count;

        foreach (var (name, locations, optimalDistance) in scenarios)
        {
            var distanceMatrix = CreateDistanceMatrixFromLocations(locations);
            var distances = new List<double>();
            var times = new List<double>();

            for (int run = 0; run < BENCHMARK_RUNS; run++)
            {
                var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);
                sw.Stop();

                var distance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);
                distances.Add(distance);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            var avgDistance = distances.Average();
            var distanceRatio = optimalDistance > 0 ? avgDistance / optimalDistance : 1.0;
            var variance = distances.Count > 1
                ? Math.Sqrt(distances.Select(d => Math.Pow(d - avgDistance, 2)).Average()) / avgDistance
                : 0.0;
            var avgTimeMs = times.Average();
            var speedPenalty = avgTimeMs > 5000 ? (avgTimeMs - 5000) / 10000.0 : 0.0;

            totalDistanceRatio += distanceRatio;
            totalConsistency += variance;
            totalSpeedPenalty += speedPenalty;

            TestContext.WriteLine($"[{name}] avg_dist={avgDistance:F2}km optimal={optimalDistance:F2}km ratio={distanceRatio:F4} " +
                                 $"cv={variance:F4} avg_time={avgTimeMs:F0}ms");
        }

        var avgDistRatio = totalDistanceRatio / scenarioCount;
        var avgConsistency = totalConsistency / scenarioCount;
        var avgSpeedPenalty = totalSpeedPenalty / scenarioCount;

        var distanceScore = Math.Max(0, avgDistRatio - 1.0);
        var consistencyScore = Math.Min(avgConsistency, 1.0);
        var speedScore = Math.Min(avgSpeedPenalty, 1.0);

        var compositeScore = WEIGHT_DISTANCE * distanceScore
                           + WEIGHT_CONSISTENCY * consistencyScore
                           + WEIGHT_SPEED * speedScore;

        TestContext.WriteLine($"");
        TestContext.WriteLine($"=== AUTORESEARCH RESULTS ===");
        TestContext.WriteLine($"distance_score={distanceScore:F4} (weight={WEIGHT_DISTANCE})");
        TestContext.WriteLine($"consistency_score={consistencyScore:F4} (weight={WEIGHT_CONSISTENCY})");
        TestContext.WriteLine($"speed_score={speedScore:F4} (weight={WEIGHT_SPEED})");
        TestContext.WriteLine($"AUTORESEARCH_SCORE: {compositeScore:F6}");

        compositeScore.ShouldBeLessThan(1.0, "composite score should be reasonable");
    }

    [Test]
    public void Benchmark_LargeScalePerformance()
    {
        var locations = CreateRandomLocations(25, seed: 42);
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);
        var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);
        sw.Stop();

        var distance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);

        TestContext.WriteLine($"[Large-25] dist={distance:F2}km time={sw.Elapsed.TotalMilliseconds:F0}ms");
        TestContext.WriteLine($"AUTORESEARCH_LARGE_TIME_MS: {sw.Elapsed.TotalMilliseconds:F0}");
        TestContext.WriteLine($"AUTORESEARCH_LARGE_DISTANCE: {distance:F2}");

        route.Count().ShouldBe(25);
    }

    [Test]
    public void Benchmark_DistanceOptimalitySmall()
    {
        var (name, locations, optimalDistance) = CreateSmallScenario();
        var distanceMatrix = CreateDistanceMatrixFromLocations(locations);

        var bestDistance = double.MaxValue;
        for (int run = 0; run < BENCHMARK_RUNS; run++)
        {
            var optimizer = new AntColonyOptimizer(distanceMatrix, _logger);
            var route = optimizer.FindOptimalRoute(fixedStart: 0, fixedEnd: 0);
            var distance = CalculateTotalDistance(route, distanceMatrix.Matrix, isRoundTrip: true);
            bestDistance = Math.Min(bestDistance, distance);
        }

        var ratio = bestDistance / optimalDistance;
        TestContext.WriteLine($"AUTORESEARCH_BEST_RATIO: {ratio:F6}");
        TestContext.WriteLine($"best={bestDistance:F4}km optimal={optimalDistance:F4}km");

        ratio.ShouldBeLessThan(1.15, "best distance should be within 15% of optimal");
    }

    private (string Name, List<Location> Locations, double OptimalDistance) CreateSmallScenario()
    {
        var locations = new List<Location>
        {
            new() { Name = "Base Liebefeld", Latitude = 46.9367, Longitude = 7.4055 },
            new() { Name = "Militaerstrasse", Latitude = 46.9550, Longitude = 7.4390 },
            new() { Name = "Thunstrasse", Latitude = 46.9420, Longitude = 7.4500 },
            new() { Name = "Bubenbergplatz", Latitude = 46.9480, Longitude = 7.4394 },
            new() { Name = "Wabern", Latitude = 46.9280, Longitude = 7.4350 }
        };

        var matrix = CreateDistanceMatrixFromLocations(locations);
        var optimal = BruteForceOptimal(matrix.Matrix, 5);
        return ("Small-5", locations, optimal);
    }

    private (string Name, List<Location> Locations, double OptimalDistance) CreateMediumScenario()
    {
        var locations = CreateRandomLocations(10, seed: 123);
        var matrix = CreateDistanceMatrixFromLocations(locations);
        var nnEstimate = NearestNeighborDistance(matrix.Matrix, 10) * 0.85;
        return ("Medium-10", locations, nnEstimate);
    }

    private (string Name, List<Location> Locations, double OptimalDistance) CreateClusteredScenario()
    {
        var random = new Random(77);
        var locations = new List<Location>();

        locations.Add(new Location { Name = "Base", Latitude = 46.9400, Longitude = 7.4200 });

        for (int i = 0; i < 4; i++)
        {
            locations.Add(new Location
            {
                Name = $"ClusterA-{i}",
                Latitude = 46.950 + random.NextDouble() * 0.005,
                Longitude = 7.440 + random.NextDouble() * 0.005
            });
        }

        for (int i = 0; i < 4; i++)
        {
            locations.Add(new Location
            {
                Name = $"ClusterB-{i}",
                Latitude = 46.920 + random.NextDouble() * 0.005,
                Longitude = 7.410 + random.NextDouble() * 0.005
            });
        }

        var matrix = CreateDistanceMatrixFromLocations(locations);
        var nnEstimate = NearestNeighborDistance(matrix.Matrix, locations.Count) * 0.80;
        return ("Clustered-9", locations, nnEstimate);
    }

    private (string Name, List<Location> Locations, double OptimalDistance) CreateLinearScenario()
    {
        var locations = new List<Location>();
        for (int i = 0; i < 8; i++)
        {
            locations.Add(new Location
            {
                Name = $"Linear-{i}",
                Latitude = 46.920 + i * 0.005,
                Longitude = 7.430
            });
        }

        var matrix = CreateDistanceMatrixFromLocations(locations);
        double linearOptimal = 0;
        for (int i = 0; i < locations.Count - 1; i++)
            linearOptimal += matrix.Matrix[i, i + 1];
        linearOptimal += matrix.Matrix[locations.Count - 1, 0];
        return ("Linear-8", locations, linearOptimal);
    }

    private List<Location> CreateRandomLocations(int count, int seed)
    {
        var random = new Random(seed);
        var locations = new List<Location>();
        for (int i = 0; i < count; i++)
        {
            locations.Add(new Location
            {
                Name = $"Loc-{i}",
                Latitude = 46.90 + random.NextDouble() * 0.08,
                Longitude = 7.40 + random.NextDouble() * 0.08
            });
        }
        return locations;
    }

    private double BruteForceOptimal(double[,] matrix, int size)
    {
        var indices = Enumerable.Range(1, size - 1).ToArray();
        double minDist = double.MaxValue;

        foreach (var perm in GetPermutations(indices))
        {
            var route = new List<int> { 0 };
            route.AddRange(perm);
            var dist = CalculateTotalDistance(route, matrix, isRoundTrip: true);
            minDist = Math.Min(minDist, dist);
        }

        return minDist;
    }

    private double NearestNeighborDistance(double[,] matrix, int size)
    {
        var visited = new HashSet<int> { 0 };
        var current = 0;
        double total = 0;

        while (visited.Count < size)
        {
            double bestDist = double.MaxValue;
            int bestNext = -1;
            for (int j = 0; j < size; j++)
            {
                if (!visited.Contains(j) && matrix[current, j] < bestDist)
                {
                    bestDist = matrix[current, j];
                    bestNext = j;
                }
            }
            if (bestNext == -1) break;
            total += bestDist;
            visited.Add(bestNext);
            current = bestNext;
        }

        total += matrix[current, 0];
        return total;
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
                if (i != j)
                {
                    var distance = HaversineKm(
                        locations[i].Latitude, locations[i].Longitude,
                        locations[j].Latitude, locations[j].Longitude);
                    matrix[i, j] = distance;
                    durationMatrix[i, j] = distance / 40.0 * 3600;
                }
            }
        }

        return new DistanceMatrix(locations, matrix, durationMatrix);
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private double CalculateTotalDistance(List<int> route, double[,] matrix, bool isRoundTrip)
    {
        double total = 0;
        for (int i = 0; i < route.Count - 1; i++)
            total += matrix[route[i], route[i + 1]];
        if (isRoundTrip && route.Count > 0)
            total += matrix[route[^1], route[0]];
        return total;
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
                yield return new[] { item }.Concat(perm);
        }
    }
}
