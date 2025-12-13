using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace UnitTest.Services.Groups;

[TestFixture]
public class GroupClientServiceCacheTests
{
    private IMemoryCache _memoryCache;

    [SetUp]
    public void SetUp()
    {
        var cacheOptions = new MemoryCacheOptions();
        _memoryCache = new MemoryCache(cacheOptions);
    }

    [TearDown]
    public void TearDown()
    {
        _memoryCache?.Dispose();
    }

    [Test]
    public void Cache_ShouldStoreAndRetrieveGroupIds()
    {
        var groupIds = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var cacheKey = "group_hierarchy_test_key";

        _memoryCache.Set(cacheKey, groupIds, TimeSpan.FromMinutes(10));

        var retrieved = _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> cachedValue);

        retrieved.Should().BeTrue();
        cachedValue.Should().NotBeNull();
        cachedValue.Should().BeEquivalentTo(groupIds);
    }

    [Test]
    public void Cache_ShouldNotRetrieveNonExistentKey()
    {
        var cacheKey = "non_existent_key";

        var retrieved = _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> cachedValue);

        retrieved.Should().BeFalse();
        cachedValue.Should().BeNull();
    }

    [Test]
    public void Cache_ShouldOverwriteExistingKey()
    {
        var cacheKey = "group_hierarchy_test_key";
        var firstGroupIds = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var secondGroupIds = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        _memoryCache.Set(cacheKey, firstGroupIds, TimeSpan.FromMinutes(10));
        _memoryCache.Set(cacheKey, secondGroupIds, TimeSpan.FromMinutes(10));

        var retrieved = _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> cachedValue);

        retrieved.Should().BeTrue();
        cachedValue.Should().BeEquivalentTo(secondGroupIds);
        cachedValue.Should().NotBeEquivalentTo(firstGroupIds);
    }

    [Test]
    public void Cache_ShouldHandleMultipleKeys()
    {
        var key1 = "group_hierarchy_key1";
        var key2 = "group_hierarchy_key2";
        var groupIds1 = new HashSet<Guid> { Guid.NewGuid() };
        var groupIds2 = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        _memoryCache.Set(key1, groupIds1, TimeSpan.FromMinutes(10));
        _memoryCache.Set(key2, groupIds2, TimeSpan.FromMinutes(10));

        _memoryCache.TryGetValue(key1, out HashSet<Guid> cached1).Should().BeTrue();
        _memoryCache.TryGetValue(key2, out HashSet<Guid> cached2).Should().BeTrue();

        cached1.Should().BeEquivalentTo(groupIds1);
        cached2.Should().BeEquivalentTo(groupIds2);
    }

    [Test]
    public void Cache_ShouldRemoveSpecificKey()
    {
        var key1 = "group_hierarchy_key1";
        var key2 = "group_hierarchy_key2";
        var groupIds1 = new HashSet<Guid> { Guid.NewGuid() };
        var groupIds2 = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        _memoryCache.Set(key1, groupIds1, TimeSpan.FromMinutes(10));
        _memoryCache.Set(key2, groupIds2, TimeSpan.FromMinutes(10));

        _memoryCache.Remove(key1);

        _memoryCache.TryGetValue(key1, out HashSet<Guid> _).Should().BeFalse();
        _memoryCache.TryGetValue(key2, out HashSet<Guid> _).Should().BeTrue();
    }

    [Test]
    public void Cache_ShouldCompactAll()
    {
        var key1 = "group_hierarchy_key1";
        var key2 = "group_hierarchy_key2";
        var groupIds1 = new HashSet<Guid> { Guid.NewGuid() };
        var groupIds2 = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        _memoryCache.Set(key1, groupIds1, TimeSpan.FromMinutes(10));
        _memoryCache.Set(key2, groupIds2, TimeSpan.FromMinutes(10));

        if (_memoryCache is MemoryCache memCache)
        {
            memCache.Compact(1.0);
        }

        _memoryCache.TryGetValue(key1, out HashSet<Guid> _).Should().BeFalse();
        _memoryCache.TryGetValue(key2, out HashSet<Guid> _).Should().BeFalse();
    }

    [Test]
    public void Cache_ShouldGenerateConsistentKeyForSortedIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var sortedIds1 = new List<Guid> { id1, id2, id3 }.OrderBy(x => x).ToList();
        var sortedIds2 = new List<Guid> { id3, id1, id2 }.OrderBy(x => x).ToList();

        var key1 = $"group_hierarchy_{string.Join("_", sortedIds1)}";
        var key2 = $"group_hierarchy_{string.Join("_", sortedIds2)}";

        key1.Should().Be(key2);
    }

    [Test]
    public void Cache_ShouldStoreDataWithExpiration()
    {
        var cacheKey = "group_hierarchy_test_key";
        var groupIds = new HashSet<Guid> { Guid.NewGuid() };
        var expiration = TimeSpan.FromMilliseconds(100);

        _memoryCache.Set(cacheKey, groupIds, expiration);

        _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> _).Should().BeTrue();

        Thread.Sleep(150);

        _memoryCache.TryGetValue(cacheKey, out HashSet<Guid> _).Should().BeFalse();
    }
}
