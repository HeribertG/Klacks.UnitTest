// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for EdgeTtsService: voice map coverage for all plugin locales via GetVoicesAsync.
/// </summary>

using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class EdgeTtsServiceTests
{
    private const string FallbackVoiceShortName = "en-US-GuyNeural";

    private static readonly string[] PluginLocales =
    {
        "ar", "cs", "da", "el", "es", "fi", "he", "id", "ja", "ko", "ms",
        "nb", "nl", "pl", "pt", "ro", "sv", "th", "vi", "zh-CN", "zh-TW"
    };

    private EdgeTtsService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new EdgeTtsService(Substitute.For<ILogger<EdgeTtsService>>());
    }

    [Test]
    public async Task GetVoicesAsync_CoversAllPluginLocales_WithoutFallingBackToEnglish()
    {
        var voices = await _service.GetVoicesAsync();

        foreach (var locale in PluginLocales)
        {
            var voice = voices.SingleOrDefault(v => v.Locale == locale);

            voice.ShouldNotBeNull($"locale '{locale}' should have a dedicated Edge voice");
            voice.VoiceId.ShouldNotBe(
                FallbackVoiceShortName,
                $"locale '{locale}' must not resolve to the English fallback voice");
        }
    }

    [Test]
    public async Task GetVoicesAsync_EachVoiceIdMatchesItsLocaleLanguage()
    {
        var voices = await _service.GetVoicesAsync();

        foreach (var voice in voices)
        {
            var languageCode = voice.Locale.Contains('-')
                ? voice.Locale[..voice.Locale.IndexOf('-')]
                : voice.Locale;

            voice.VoiceId.ShouldStartWith(
                languageCode,
                customMessage: $"voice '{voice.VoiceId}' should belong to language '{languageCode}'");
        }
    }
}
