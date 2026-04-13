// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DictionaryService context building from DB entries and caching.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant;

using FluentAssertions;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
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
    private ITranscriptionDictionaryRepository _repository;

    [SetUp]
    public void SetUp()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _logger = Substitute.For<ILogger<DictionaryService>>();
        _repository = Substitute.For<ITranscriptionDictionaryRepository>();

        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new TranscriptionDictionaryEntry { CorrectTerm = "Klacks", Category = "app", PhoneticVariants = ["Klax", "Klags", "Clacks"] },
            new TranscriptionDictionaryEntry { CorrectTerm = "Klacksy", Category = "app", PhoneticVariants = ["Klaksi", "Clacksy", "Klaksy"] },
            new TranscriptionDictionaryEntry { CorrectTerm = "FD", Category = "shift", Description = "Frühdienst" },
        ]);

        _service = new DictionaryService(_repository, _cache, _logger);
    }

    [Test]
    public async Task BuildContextAsync_ShouldIncludeEntries()
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
        await _repository.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }
}
