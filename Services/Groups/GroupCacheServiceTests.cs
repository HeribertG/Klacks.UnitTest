using FluentAssertions;
using Klacks.Api.Domain.Services.Groups;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace UnitTest.Services.Groups;

[TestFixture]
public class GroupCacheServiceTests
{
    private IMemoryCache _memoryCache;
    private GroupCacheService _groupCacheService;

    [SetUp]
    public void SetUp()
    {
        var options = new MemoryCacheOptions();
        _memoryCache = new MemoryCache(options);
        _groupCacheService = new GroupCacheService(_memoryCache);
    }

    [TearDown]
    public void TearDown()
    {
        _memoryCache?.Dispose();
    }

    [Test]
    public void InvalidateGroupHierarchyCache_ShouldClearAllCacheEntries()
    {
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();
        var cacheKey1 = $"group_hierarchy_{groupId1}";
        var cacheKey2 = $"group_hierarchy_{groupId2}";

        var testData1 = new HashSet<Guid> { groupId1, Guid.NewGuid() };
        var testData2 = new HashSet<Guid> { groupId2, Guid.NewGuid() };

        _memoryCache.Set(cacheKey1, testData1);
        _memoryCache.Set(cacheKey2, testData2);

        _memoryCache.TryGetValue(cacheKey1, out HashSet<Guid> _).Should().BeTrue();
        _memoryCache.TryGetValue(cacheKey2, out HashSet<Guid> _).Should().BeTrue();

        _groupCacheService.InvalidateGroupHierarchyCache();

        _memoryCache.TryGetValue(cacheKey1, out HashSet<Guid> _).Should().BeFalse();
        _memoryCache.TryGetValue(cacheKey2, out HashSet<Guid> _).Should().BeFalse();
    }

    [Test]
    public void InvalidateGroupCache_ShouldRemoveSpecificCacheEntry()
    {
        var groupId = Guid.NewGuid();
        var otherGroupId = Guid.NewGuid();
        var cacheKey = $"group_hierarchy_{groupId}";
        var otherCacheKey = $"group_hierarchy_{otherGroupId}";

        var testData = new HashSet<Guid> { groupId, Guid.NewGuid() };
        var otherTestData = new HashSet<Guid> { otherGroupId, Guid.NewGuid() };

        _memoryCache.Set(cacheKey, testData);
        _memoryCache.Set(otherCacheKey, otherTestData);

        _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> _).Should().BeTrue();
        _memoryCache.TryGetValue(otherCacheKey, out HashSet<Guid> _).Should().BeTrue();

        _groupCacheService.InvalidateGroupCache(groupId);

        _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> _).Should().BeFalse();
        _memoryCache.TryGetValue(otherCacheKey, out HashSet<Guid> _).Should().BeTrue();
    }

    [Test]
    public void InvalidateGroupCache_WithNonExistentKey_ShouldNotThrow()
    {
        var groupId = Guid.NewGuid();

        Action act = () => _groupCacheService.InvalidateGroupCache(groupId);

        act.Should().NotThrow();
    }

    [Test]
    public void InvalidateGroupHierarchyCache_WhenCacheIsEmpty_ShouldNotThrow()
    {
        Action act = () => _groupCacheService.InvalidateGroupHierarchyCache();

        act.Should().NotThrow();
    }
}
