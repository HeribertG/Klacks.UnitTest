// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules;
using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// A double apply used to materialise the same run twice because reading and invalidating the cache
/// were two separate steps. TryTake closes that window.
/// </summary>
[TestFixture]
public sealed class ResultCacheTryTakeTests
{
    [Test]
    public void WizardResultCache_TryTake_SucceedsOnceAndConsumesTheEntry()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        var scenario = new CoreScenario { Id = "s" };
        cache.Store(jobId, scenario, analyseToken: null);

        cache.TryTake(jobId, out var first, out _, out _, out _, out _).ShouldBeTrue();
        first.ShouldBe(scenario);

        cache.TryTake(jobId, out var second, out _, out _, out _, out _).ShouldBeFalse();
        second.ShouldBeNull();
        cache.TryGet(jobId, out _, out _, out _, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void WizardResultCache_ReStoreAfterTake_MakesTheEntryAvailableAgain()
    {
        var cache = new WizardResultCache();
        var jobId = Guid.NewGuid();
        var scenario = new CoreScenario { Id = "s" };
        cache.Store(jobId, scenario, analyseToken: null);

        cache.TryTake(jobId, out var taken, out var token, out var escalations, out var subScore, out var violations)
            .ShouldBeTrue();
        cache.Store(jobId, taken!, token, escalations, subScore, violations);

        cache.TryTake(jobId, out var again, out _, out _, out _, out _).ShouldBeTrue();
        again.ShouldBe(scenario);
    }

    [Test]
    public void HarmonizerResultCache_TryTake_SucceedsOnceAndConsumesTheEntry()
    {
        var cache = new HarmonizerResultCache();
        var jobId = Guid.NewGuid();
        var original = BuildBitmap();
        var best = BuildBitmap();
        cache.Store(jobId, original, best, sourceAnalyseToken: null);

        cache.TryTake(jobId, out var takenOriginal, out var takenBest, out _, out _, out _, out _).ShouldBeTrue();
        takenOriginal.ShouldBe(original);
        takenBest.ShouldBe(best);

        cache.TryTake(jobId, out _, out var secondBest, out _, out _, out _, out _).ShouldBeFalse();
        secondBest.ShouldBeNull();
    }

    [Test]
    public void HarmonizerResultCache_TryGet_StillLeavesTheEntryInPlace()
    {
        var cache = new HarmonizerResultCache();
        var jobId = Guid.NewGuid();
        cache.Store(jobId, BuildBitmap(), BuildBitmap(), sourceAnalyseToken: null);

        cache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeTrue();
        cache.TryGet(jobId, out _, out _, out _, out _, out _, out _).ShouldBeTrue();
        cache.TryTake(jobId, out _, out _, out _, out _, out _, out _).ShouldBeTrue();
    }

    private static HarmonyBitmap BuildBitmap()
    {
        var agents = new List<BitmapAgent>
        {
            new("agent-0", "Agent 0", 100m, new HashSet<CellSymbol>()),
        };
        var input = new BitmapInput(agents, new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 8), []);
        return BitmapBuilder.Build(input);
    }
}
