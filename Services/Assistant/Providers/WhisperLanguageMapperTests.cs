// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Services.Assistant.Providers;

using Klacks.Api.Infrastructure.Services.Assistant.Providers.Stt;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class WhisperLanguageMapperTests
{
    [TestCase("de", "de")]
    [TestCase("DE-CH", "de")]
    [TestCase("zh-CN", "zh")]
    [TestCase("zh-TW", "zh")]
    [TestCase("nb", "no")]
    [TestCase("nn", "no")]
    [TestCase("he", "he")]
    [TestCase("pt", "pt")]
    public void ToWhisperLanguage_ShouldMapLocaleToWhisperCode(string locale, string expected)
    {
        WhisperLanguageMapper.ToWhisperLanguage(locale).ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ToWhisperLanguage_ShouldReturnEmpty_ForMissingLocale(string? locale)
    {
        WhisperLanguageMapper.ToWhisperLanguage(locale).ShouldBe(string.Empty);
    }
}
