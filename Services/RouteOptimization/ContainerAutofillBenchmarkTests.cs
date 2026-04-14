// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Autoresearch benchmark for ContainerAutofillAlgorithm.
/// Measures how much onSiteTime the greedy algorithm collects vs. theoretical maximum.
/// Output: AUTOFILL_SCORE (lower = better, goal below 0.20).
/// </summary>

using FluentAssertions;
using Klacks.Api.Domain.Services.RouteOptimization;

namespace Klacks.UnitTest.Services.RouteOptimization;

[TestFixture]
[Category("AutoresearchAutofill")]
public class ContainerAutofillBenchmarkTests
{
    private const int BENCHMARK_RUNS = 5;
    private const double WEIGHT_QUALITY = 0.60;
    private const double WEIGHT_CONSISTENCY = 0.20;
    private const double WEIGHT_SPEED = 0.20;

    [Test]
    public void Benchmark_CompositeScore()
    {
        var scenarios = new[]
        {
            CreateMorningAfternoonScenario(),
            CreateTightWindowScenario(),
            CreateMixedWorkTimeScenario(),
            CreateHighDensityScenario()
        };

        var totalQuality = 0.0;
        var totalConsistency = 0.0;
        var totalSpeedPenalty = 0.0;
        var scenarioCount = scenarios.Length;

        foreach (var (name, locations, durationMatrix, fromSec, endSec, theoretical) in scenarios)
        {
            var collected = new List<double>();
            var times = new List<double>();
            var n = locations.Count;
            var startIdx = n - 1;
            var endIdx = n - 1;

            for (int run = 0; run < BENCHMARK_RUNS; run++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var selected = ContainerAutofillAlgorithm.GreedySelect(
                    durationMatrix, locations, startIdx, endIdx, fromSec, endSec);
                var route = ContainerAutofillAlgorithm.PostInsert(
                    durationMatrix, locations, selected,
                    Enumerable.Range(0, n - 1).Except(selected).ToList(),
                    startIdx, endIdx, fromSec, endSec,
                    endSec - fromSec);
                sw.Stop();

                var onSite = route.Sum(i => locations[i].TotalOnSiteTime.TotalSeconds / 60.0);
                collected.Add(onSite);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            var avgCollected = collected.Average();
            var qualityScore = theoretical > 0 ? Math.Max(0, 1.0 - avgCollected / theoretical) : 0;
            var variance = collected.Count > 1
                ? Math.Sqrt(collected.Select(d => Math.Pow(d - avgCollected, 2)).Average()) / Math.Max(avgCollected, 1)
                : 0.0;
            var speedPenalty = times.Average() > 1000 ? (times.Average() - 1000) / 5000.0 : 0.0;

            totalQuality += qualityScore;
            totalConsistency += variance;
            totalSpeedPenalty += speedPenalty;

            TestContext.WriteLine($"[{name}] collected={avgCollected:F1}min theoretical={theoretical:F1}min " +
                                 $"quality={1 - qualityScore:F4} cv={variance:F4} avg_time={times.Average():F1}ms");
        }

        var avgQuality = totalQuality / scenarioCount;
        var avgConsistency = totalConsistency / scenarioCount;
        var avgSpeed = Math.Min(totalSpeedPenalty / scenarioCount, 1.0);

        var compositeScore = WEIGHT_QUALITY * avgQuality
                           + WEIGHT_CONSISTENCY * avgConsistency
                           + WEIGHT_SPEED * avgSpeed;

        TestContext.WriteLine("");
        TestContext.WriteLine("=== AUTOFILL AUTORESEARCH RESULTS ===");
        TestContext.WriteLine($"quality_score={avgQuality:F4} (weight={WEIGHT_QUALITY}) [0=max collection, 1=nothing collected]");
        TestContext.WriteLine($"consistency_score={avgConsistency:F4} (weight={WEIGHT_CONSISTENCY})");
        TestContext.WriteLine($"speed_score={avgSpeed:F4} (weight={WEIGHT_SPEED})");
        TestContext.WriteLine($"AUTOFILL_SCORE: {compositeScore:F6}");

        compositeScore.Should().BeLessThan(1.0, "composite score should be reasonable");
    }

    [Test]
    public void Benchmark_FillRate()
    {
        var (name, locations, durationMatrix, fromSec, endSec, _) = CreateHighDensityScenario();
        var n = locations.Count;
        var startIdx = n - 1;

        var selected = ContainerAutofillAlgorithm.GreedySelect(
            durationMatrix, locations, startIdx, startIdx, fromSec, endSec);
        var route = ContainerAutofillAlgorithm.PostInsert(
            durationMatrix, locations, selected,
            Enumerable.Range(0, n - 1).Except(selected).ToList(),
            startIdx, startIdx, fromSec, endSec, endSec - fromSec);

        var fillRate = (double)route.Count / (n - 1);
        TestContext.WriteLine($"AUTOFILL_FILL_RATE: {fillRate:F4} ({route.Count}/{n - 1} shifts)");

        fillRate.Should().BeGreaterThan(0.1, "at least 10% of shifts should be selected");
    }

    private static (string name, List<Location> locations, double[,] duration,
        double fromSec, double endSec, double theoreticalMin)
    CreateMorningAfternoonScenario()
    {
        var locations = new List<Location>();
        var random = new Random(42);
        const double from = 8 * 3600;
        const double end = 17 * 3600;

        for (int i = 0; i < 12; i++)
        {
            var lat = 46.90 + random.NextDouble() * 0.06;
            var lon = 7.40 + random.NextDouble() * 0.06;
            var workHours = 1.0 + random.NextDouble() * 2.0;
            var windowStart = i < 6 ? TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)) : TimeOnly.FromTimeSpan(TimeSpan.FromHours(13));
            var windowEnd = i < 6 ? TimeOnly.FromTimeSpan(TimeSpan.FromHours(12)) : TimeOnly.FromTimeSpan(TimeSpan.FromHours(17));

            locations.Add(new Location
            {
                Name = $"Shift-{i}",
                Latitude = lat,
                Longitude = lon,
                ShiftId = Guid.NewGuid(),
                WorkTime = TimeSpan.FromHours(workHours),
                BriefingTime = TimeSpan.FromMinutes(15),
                DebriefingTime = TimeSpan.FromMinutes(15),
                TimeRangeStart = windowStart,
                TimeRangeEnd = windowEnd
            });
        }

        var base_ = new Location { Name = "Base", Latitude = 46.94, Longitude = 7.42, ShiftId = Guid.Empty };
        locations.Add(base_);

        var duration = BuildDurationMatrix(locations);
        var theoretical = ComputeTheoreticalMax(locations.Take(12).ToList(), from, end);
        return ("MorningAfternoon-12", locations, duration, from, end, theoretical);
    }

    private static (string name, List<Location> locations, double[,] duration,
        double fromSec, double endSec, double theoreticalMin)
    CreateTightWindowScenario()
    {
        var locations = new List<Location>();
        var random = new Random(7);
        const double from = 8 * 3600;
        const double end = 16 * 3600;

        for (int i = 0; i < 10; i++)
        {
            var lat = 46.92 + random.NextDouble() * 0.04;
            var lon = 7.41 + random.NextDouble() * 0.04;
            var windowStartH = 8 + i * 0.7;
            var windowEndH = windowStartH + 1.5;

            locations.Add(new Location
            {
                Name = $"Tight-{i}",
                Latitude = lat,
                Longitude = lon,
                ShiftId = Guid.NewGuid(),
                WorkTime = TimeSpan.FromHours(1.0),
                BriefingTime = TimeSpan.FromMinutes(10),
                DebriefingTime = TimeSpan.FromMinutes(10),
                TimeRangeStart = TimeOnly.FromTimeSpan(TimeSpan.FromHours(windowStartH)),
                TimeRangeEnd = TimeOnly.FromTimeSpan(TimeSpan.FromHours(Math.Min(windowEndH, 16)))
            });
        }

        var base_ = new Location { Name = "Base", Latitude = 46.93, Longitude = 7.43, ShiftId = Guid.Empty };
        locations.Add(base_);

        var duration = BuildDurationMatrix(locations);
        var theoretical = ComputeTheoreticalMax(locations.Take(10).ToList(), from, end);
        return ("TightWindows-10", locations, duration, from, end, theoretical);
    }

    private static (string name, List<Location> locations, double[,] duration,
        double fromSec, double endSec, double theoreticalMin)
    CreateMixedWorkTimeScenario()
    {
        var locations = new List<Location>();
        var random = new Random(99);
        const double from = 6 * 3600;
        const double end = 22 * 3600;

        var workTimes = new[] { 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 0.75, 1.25, 2.5, 3.5, 1.0, 2.0, 0.5, 1.5, 3.0 };
        for (int i = 0; i < workTimes.Length; i++)
        {
            locations.Add(new Location
            {
                Name = $"Mixed-{i}",
                Latitude = 46.90 + random.NextDouble() * 0.08,
                Longitude = 7.38 + random.NextDouble() * 0.08,
                ShiftId = Guid.NewGuid(),
                WorkTime = TimeSpan.FromHours(workTimes[i]),
                BriefingTime = TimeSpan.FromMinutes(15),
                DebriefingTime = TimeSpan.FromMinutes(10),
                TimeRangeStart = TimeOnly.FromTimeSpan(TimeSpan.FromHours(6 + random.NextDouble() * 8)),
                TimeRangeEnd = TimeOnly.FromTimeSpan(TimeSpan.FromHours(14 + random.NextDouble() * 8))
            });
        }

        var base_ = new Location { Name = "Base", Latitude = 46.94, Longitude = 7.42, ShiftId = Guid.Empty };
        locations.Add(base_);

        var duration = BuildDurationMatrix(locations);
        var theoretical = ComputeTheoreticalMax(locations.Take(workTimes.Length).ToList(), from, end);
        return ("MixedWorkTimes-15", locations, duration, from, end, theoretical);
    }

    private static (string name, List<Location> locations, double[,] duration,
        double fromSec, double endSec, double theoreticalMin)
    CreateHighDensityScenario()
    {
        var locations = new List<Location>();
        var random = new Random(55);
        const double from = 7 * 3600;
        const double end = 19 * 3600;

        for (int i = 0; i < 20; i++)
        {
            locations.Add(new Location
            {
                Name = $"Dense-{i}",
                Latitude = 46.93 + random.NextDouble() * 0.02,
                Longitude = 7.42 + random.NextDouble() * 0.02,
                ShiftId = Guid.NewGuid(),
                WorkTime = TimeSpan.FromHours(1.0 + random.NextDouble() * 2.0),
                BriefingTime = TimeSpan.FromMinutes(15),
                DebriefingTime = TimeSpan.FromMinutes(15),
                TimeRangeStart = null,
                TimeRangeEnd = null
            });
        }

        var base_ = new Location { Name = "Base", Latitude = 46.94, Longitude = 7.42, ShiftId = Guid.Empty };
        locations.Add(base_);

        var duration = BuildDurationMatrix(locations);
        var theoretical = ComputeTheoreticalMax(locations.Take(20).ToList(), from, end);
        return ("HighDensity-20", locations, duration, from, end, theoretical);
    }

    private static double ComputeTheoreticalMax(List<Location> candidates, double fromSec, double endSec)
    {
        var budget = endSec - fromSec;
        var sorted = candidates
            .Select(l => l.TotalOnSiteTime.TotalSeconds)
            .OrderByDescending(s => s)
            .ToList();

        double remaining = budget;
        double total = 0;
        foreach (var s in sorted)
        {
            if (remaining <= 0) break;
            var take = Math.Min(s, remaining);
            total += take;
            remaining -= s;
        }

        return total / 60.0;
    }

    private static double[,] BuildDurationMatrix(List<Location> locations)
    {
        var n = locations.Count;
        var matrix = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i != j)
                {
                    var km = HaversineKm(locations[i].Latitude, locations[i].Longitude,
                                        locations[j].Latitude, locations[j].Longitude);
                    matrix[i, j] = km / 40.0 * 3600;
                }
            }
        }
        return matrix;
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
}
