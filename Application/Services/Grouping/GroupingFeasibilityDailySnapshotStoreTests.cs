// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the generation guard of the daily snapshot store: a snapshot computed from a read at generation g
/// is stored only while the generation is still g, so an analysis that raced a successful apply (which
/// removes the snapshot and bumps the generation) cannot put its stale result back into the cache.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Microsoft.Extensions.Caching.Memory;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingFeasibilityDailySnapshotStoreTests
{
    private const int CacheSizeLimit = 10;
    private const string DayKey = "2026-09-28";
    private const string OtherDayKey = "2026-09-29";

    private MemoryCache _cache = null!;
    private GroupingFeasibilityDailySnapshotStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimit });
        _sut = new GroupingFeasibilityDailySnapshotStore(_cache);
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private static GroupingFeasibilityDailySnapshot Snapshot(string fingerprint) =>
        new(fingerprint, new GroupingFeasibilityCounts(1, 0, 0, 0), HasReportFindings: true);

    [Test]
    public void SetAtTheReadGeneration_StoresTheSnapshotForThatDayOnly()
    {
        _sut.TryGet(DayKey, out var generation).ShouldBeNull();

        _sut.Set(DayKey, Snapshot("a"), generation).ShouldBeTrue();

        _sut.TryGet(DayKey, out _)!.ReportFingerprint.ShouldBe("a");
        _sut.TryGet(OtherDayKey, out _).ShouldBeNull();
    }

    [Test]
    public void Remove_BumpsTheGeneration()
    {
        var before = _sut.CurrentGeneration;

        _sut.Remove(DayKey);

        _sut.CurrentGeneration.ShouldBeGreaterThan(before);
    }

    [Test]
    public void RaceWithApply_SnapshotComputedBeforeTheRemoveIsNotStored()
    {
        _sut.TryGet(DayKey, out var readGeneration).ShouldBeNull();
        _sut.Remove(DayKey);

        _sut.Set(DayKey, Snapshot("stale"), readGeneration).ShouldBeFalse();

        _sut.TryGet(DayKey, out _).ShouldBeNull();
    }

    [Test]
    public void RaceWithApply_StaleSnapshotDoesNotOverwriteAFreshOneEither()
    {
        _sut.TryGet(DayKey, out var staleGeneration);
        _sut.Remove(DayKey);
        _sut.TryGet(DayKey, out var freshGeneration);
        _sut.Set(DayKey, Snapshot("fresh"), freshGeneration).ShouldBeTrue();

        _sut.Set(DayKey, Snapshot("stale"), staleGeneration).ShouldBeFalse();

        _sut.TryGet(DayKey, out _)!.ReportFingerprint.ShouldBe("fresh");
    }

    [Test]
    public void SetWithoutAnInterveningRemove_OverwritesTheEarlierSnapshot()
    {
        _sut.TryGet(DayKey, out var generation);
        _sut.Set(DayKey, Snapshot("first"), generation).ShouldBeTrue();

        _sut.Set(DayKey, Snapshot("second"), _sut.CurrentGeneration).ShouldBeTrue();

        _sut.TryGet(DayKey, out _)!.ReportFingerprint.ShouldBe("second");
    }
}
