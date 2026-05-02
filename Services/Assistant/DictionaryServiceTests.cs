// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DictionaryService context building from DB entries and caching.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant;

using Shouldly;
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

        result.ShouldContain("Klacks");
        result.ShouldContain("FD");
        result.ShouldContain("Frühdienst");
    }

    [Test]
    public async Task BuildContextAsync_ShouldIncludePhoneticVariants()
    {
        var result = await _service.BuildContextAsync();

        result.ShouldContain("Klax");
        result.ShouldContain("Klaksi");
    }

    [Test]
    public async Task BuildContextAsync_ShouldReturnCachedResult_OnSecondCall()
    {
        var result1 = await _service.BuildContextAsync();
        var result2 = await _service.BuildContextAsync();

        result1.ShouldBe(result2);
        await _repository.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyReplacementsAsync_ShouldReplacePhoneticVariantsCaseInsensitively()
    {
        var result = await _service.ApplyReplacementsAsync("Hallo Klaksi, kannst du Klags öffnen?");

        result.ShouldBe("Hallo Klacksy, kannst du Klacks öffnen?");
    }

    [Test]
    public async Task ApplyReplacementsAsync_ShouldRespectWordBoundaries()
    {
        var result = await _service.ApplyReplacementsAsync("Klax-Bericht und Klaxonen");

        result.ShouldBe("Klacks-Bericht und Klaxonen");
    }

    [Test]
    public async Task ApplyReplacementsAsync_ShouldReturnInputUnchanged_WhenNoVariantsMatch()
    {
        var result = await _service.ApplyReplacementsAsync("Guten Morgen!");

        result.ShouldBe("Guten Morgen!");
    }

    [Test]
    public async Task ApplyReplacementsAsync_ShouldReturnEmptyString_WhenInputIsEmpty()
    {
        var result = await _service.ApplyReplacementsAsync(string.Empty);

        result.ShouldBe(string.Empty);
    }

    [Test]
    public async Task InvalidateCache_ShouldForceReloadFromRepository_OnNextCall()
    {
        await _service.BuildContextAsync();
        _service.InvalidateCache();
        await _service.BuildContextAsync();

        await _repository.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }
}
