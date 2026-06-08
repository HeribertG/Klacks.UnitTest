// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the phonetic fuzzy pass in DictionaryService.ApplyReplacementsAsync.
/// </summary>
namespace Klacks.UnitTest.Services.Assistant;

using Shouldly;
using NSubstitute;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Phonetics;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

[TestFixture]
public class DictionaryServiceFuzzyTests
{
    private ITranscriptionDictionaryRepository _repository;
    private IPhoneticConfigProvider _configProvider;
    private DictionaryService _service;

    [SetUp]
    public void SetUp()
    {
        var entries = new List<TranscriptionDictionaryEntry>
        {
            new()
            {
                Id = Guid.NewGuid(),
                CorrectTerm = "Klacksy",
                PhoneticVariants = ["Kluxi"],
                Language = "de",
            },
        };

        _repository = Substitute.For<ITranscriptionDictionaryRepository>();
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        _configProvider = Substitute.For<IPhoneticConfigProvider>();
        _configProvider.GetForLocale(Arg.Any<string?>()).Returns(new PhoneticConfig
        {
            Enabled = true,
            Encoder = "koelner",
            MinWordLength = 4,
            MaxEditDistance = 4,
        });

        _service = new DictionaryService(
            _repository,
            new MemoryCache(new MemoryCacheOptions()),
            new PhoneticEncoderFactory(),
            _configProvider,
            NullLogger<DictionaryService>.Instance);
    }

    [Test]
    public async Task ApplyReplacements_ShouldFuzzyCorrect_UnlistedSoundAlike()
    {
        var result = await _service.ApplyReplacementsAsync("Sag Klaxi etwas", "de");

        result.ShouldBe("Sag Klacksy etwas");
    }

    [Test]
    public async Task ApplyReplacements_ShouldStillReplace_ExactVariant()
    {
        var result = await _service.ApplyReplacementsAsync("Sag Kluxi etwas", "de");

        result.ShouldBe("Sag Klacksy etwas");
    }

    [Test]
    public async Task ApplyReplacements_ShouldNotOverCorrect_UnrelatedWords()
    {
        var result = await _service.ApplyReplacementsAsync("Hallo Tisch und Stuhl", "de");

        result.ShouldBe("Hallo Tisch und Stuhl");
    }

    [Test]
    public async Task ApplyReplacements_ShouldNotTouch_ShortWords()
    {
        var result = await _service.ApplyReplacementsAsync("Sag mir was", "de");

        result.ShouldBe("Sag mir was");
    }
}
