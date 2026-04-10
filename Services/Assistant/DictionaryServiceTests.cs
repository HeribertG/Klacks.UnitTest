// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DictionaryService context building with static entries and caching.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant;

using FluentAssertions;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
public class DictionaryServiceTests
{
    private DictionaryService _service;
    private IMemoryCache _cache;
    private ILogger<DictionaryService> _logger;

    [SetUp]
    public void SetUp()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _logger = Substitute.For<ILogger<DictionaryService>>();
        _service = new DictionaryService(_cache, _logger);
    }

    [Test]
    public async Task BuildContextAsync_ShouldIncludeStaticEntries()
    {
        var result = await _service.BuildContextAsync();

        result.Should().Contain("Klacks");
        result.Should().Contain("FD");
        result.Should().Contain("Frühdienst");
    }

    [Test]
    public async Task BuildContextAsync_ShouldIncludePhoneticVariants()
    {
        var result = await _service.BuildContextAsync();

        result.Should().Contain("Klax");
        result.Should().Contain("Klaksi");
    }

    [Test]
    public async Task BuildContextAsync_ShouldReturnCachedResult_OnSecondCall()
    {
        var result1 = await _service.BuildContextAsync();
        var result2 = await _service.BuildContextAsync();

        result1.Should().Be(result2);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }
}
