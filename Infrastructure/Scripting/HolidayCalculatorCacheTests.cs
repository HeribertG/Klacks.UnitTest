// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the singleton HolidayCalculatorCache: entries are reused per selection and year, invalidation
/// drops them, and a calculator whose factory was still loading while an invalidation ran is handed to its
/// caller but never stored - otherwise the pre-change calendar rules would stay cached until restart.
/// Only the generation check before GetOrAdd is exercised; the re-check after GetOrAdd (invalidation landing
/// between check and add) cannot be triggered deterministically without a test hook and stays untested.
/// </summary>

using Klacks.Api.Domain.Services.Holidays;
using Klacks.Api.Infrastructure.Scripting;

namespace Klacks.UnitTest.Infrastructure.Scripting;

[TestFixture]
public class HolidayCalculatorCacheTests
{
    private const int TestYear = 2026;

    private HolidayCalculatorCache _cache = null!;
    private Guid _selectionId;

    [SetUp]
    public void SetUp()
    {
        _cache = new HolidayCalculatorCache();
        _selectionId = Guid.NewGuid();
    }

    [Test]
    public async Task GetOrCreateAsync_SecondCall_ReusesCachedCalculator()
    {
        var factoryCalls = 0;

        var first = await _cache.GetOrCreateAsync(_selectionId, TestYear, () => CreateAsync(ref factoryCalls));
        var second = await _cache.GetOrCreateAsync(_selectionId, TestYear, () => CreateAsync(ref factoryCalls));

        second.ShouldBeSameAs(first);
        factoryCalls.ShouldBe(1);
    }

    [Test]
    public async Task GetOrCreateAsync_AfterInvalidateAll_RebuildsCalculator()
    {
        var factoryCalls = 0;
        var first = await _cache.GetOrCreateAsync(_selectionId, TestYear, () => CreateAsync(ref factoryCalls));

        _cache.InvalidateAll();
        var second = await _cache.GetOrCreateAsync(_selectionId, TestYear, () => CreateAsync(ref factoryCalls));

        second.ShouldNotBeSameAs(first);
        factoryCalls.ShouldBe(2);
    }

    [Test]
    public async Task GetOrCreateAsync_InvalidateAllWhileFactoryLoads_DoesNotStoreStaleCalculator()
    {
        var stale = new HolidaysListCalculator { CurrentYear = TestYear };

        var returned = await _cache.GetOrCreateAsync(_selectionId, TestYear, () =>
        {
            _cache.InvalidateAll();
            return Task.FromResult<IHolidaysListCalculator>(stale);
        });

        returned.ShouldBeSameAs(stale);
        var fresh = new HolidaysListCalculator { CurrentYear = TestYear };
        var next = await _cache.GetOrCreateAsync(_selectionId, TestYear, () => Task.FromResult<IHolidaysListCalculator>(fresh));
        next.ShouldBeSameAs(fresh);
    }

    [Test]
    public async Task GetOrCreateAsync_InvalidateSelectionWhileFactoryLoads_DoesNotStoreStaleCalculator()
    {
        var stale = new HolidaysListCalculator { CurrentYear = TestYear };

        await _cache.GetOrCreateAsync(_selectionId, TestYear, () =>
        {
            _cache.Invalidate(_selectionId);
            return Task.FromResult<IHolidaysListCalculator>(stale);
        });

        var fresh = new HolidaysListCalculator { CurrentYear = TestYear };
        var next = await _cache.GetOrCreateAsync(_selectionId, TestYear, () => Task.FromResult<IHolidaysListCalculator>(fresh));
        next.ShouldBeSameAs(fresh);
    }

    [Test]
    public void GetOrCreate_InvalidateAllWhileFactoryLoads_DoesNotStoreStaleCalculator()
    {
        var stale = new HolidaysListCalculator { CurrentYear = TestYear };

        _cache.GetOrCreate(_selectionId, TestYear, () =>
        {
            _cache.InvalidateAll();
            return stale;
        });

        var fresh = new HolidaysListCalculator { CurrentYear = TestYear };
        _cache.GetOrCreate(_selectionId, TestYear, () => fresh).ShouldBeSameAs(fresh);
    }

    private static Task<IHolidaysListCalculator> CreateAsync(ref int factoryCalls)
    {
        factoryCalls++;
        return Task.FromResult<IHolidaysListCalculator>(new HolidaysListCalculator { CurrentYear = TestYear });
    }
}
