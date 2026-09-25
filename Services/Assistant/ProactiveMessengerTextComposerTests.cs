// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProactiveMessengerTextComposer — verifies that a messenger message carries a
/// readable sentence in the installation language instead of the raw i18n key, that the language
/// comes from the DEFAULT_LANGUAGE setting with a safe fallback, that an unsupported or unreadable
/// setting never throws inside the dispatch loop, and that a key outside the small server-side
/// catalogue degrades to the key-plus-values form rather than to nothing. Each of the four core languages
/// has its own sentence, a language-pack language (ja, zh-CN, a regional tag) is rendered from the pack's
/// translations.json, an unknown language is English, a loaded pack that lacks the key falls back to English
/// with a warning, and a value that looks like a placeholder is never expanded twice.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ProactiveMessengerTextComposerTests
{
    private const string Japanese = "ja";
    private const string ChineseSimplified = "zh-CN";
    private const string PackWithoutTheKey = "xx-Partial";
    private const string CounterValue = "3";
    private const string ApiDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const string TranslationsFile = "translations.json";

    private ISettingsReader _settingsReader = null!;
    private RecordingLogger<InstallationLanguageResolver> _resolverLogger = null!;
    private RecordingLogger<ProactiveMessengerTextComposer> _logger = null!;
    private ProactiveMessengerTextComposer _sut = null!;

    [SetUp]
    public void Setup()
    {
        MessengerProactiveTexts.Reset();
        _settingsReader = Substitute.For<ISettingsReader>();
        SetInstallationLanguage(null);
        _resolverLogger = new RecordingLogger<InstallationLanguageResolver>();
        _logger = new RecordingLogger<ProactiveMessengerTextComposer>();
        _sut = new ProactiveMessengerTextComposer(new InstallationLanguageResolver(_settingsReader, _resolverLogger), _logger);
    }

    [TearDown]
    public void ResetConfiguredTexts() => MessengerProactiveTexts.Reset();

    private void SetInstallationLanguage(string? language) =>
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(language == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [SettingKeys.DefaultLanguage] = language });

    private static string ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null
               && !Directory.Exists(Path.Combine(directory.FullName, ApiDirectory, PluginsDirectory, LanguagesDirectory)))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull();
        return Path.Combine(directory.FullName, ApiDirectory);
    }

    private static void LoadThePacks()
    {
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    private static string PackSentence(string pack, string key, string date, string days)
    {
        var file = Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory, pack, TranslationsFile);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))![key]
            .Replace("{{date}}", date)
            .Replace("{{days}}", days);
    }

    private sealed record CatalogueEvent(string Key, IReadOnlyDictionary<string, string>? Params) : IAgentTriggerEvent
    {
        public string Kind => AgentTriggerKinds.UnstaffedShift;
        public string Severity => AgentTriggerSeverity.High;
        public string Summary => ProactiveMessageMarkers.I18nPrefix + Key;
        public IReadOnlyDictionary<string, string>? SummaryParams => Params;
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
    }

    private static CatalogueEvent UnstaffedShift(string date = "16.08.2026", string days = "1") =>
        new(ProactiveMessageI18nKeys.UnstaffedShift, new Dictionary<string, string> { ["date"] = date, ["days"] = days });

    [Test]
    public async Task ComposeAsync_GermanInstallation_RendersTheGermanSentenceWithItsValues()
    {
        SetInstallationLanguage("de");

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Is.EqualTo("Eine Schicht am 16.08.2026 (in 1 Tag(en)) ist noch unbesetzt."));
    }

    [Test]
    public async Task ComposeAsync_FrenchInstallation_RendersTheFrenchSentence()
    {
        SetInstallationLanguage("fr");

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Does.StartWith("Un service le 16.08.2026"));
    }

    [TestCase("de", "Eine Schicht am 16.08.2026 (in 1 Tag(en)) ist noch unbesetzt.")]
    [TestCase("en", "A shift on 16.08.2026 (in 1 day(s)) is still unstaffed.")]
    [TestCase("fr", "Un service le 16.08.2026 (dans 1 jour(s)) n'est toujours pas pourvu.")]
    [TestCase("it", "Un turno il 16.08.2026 (tra 1 giorno/i) è ancora scoperto.")]
    public async Task ComposeAsync_EachCoreLanguage_RendersItsOwnSentence(string language, string expected)
    {
        LoadThePacks();
        SetInstallationLanguage(language);

        var text = await _sut.ComposeAsync(UnstaffedShift());

        text.ShouldBe(expected);
    }

    [TestCase(Japanese, Japanese)]
    [TestCase(ChineseSimplified, ChineseSimplified)]
    [TestCase("zh-cn", ChineseSimplified)]
    [TestCase("ja-JP", Japanese)]
    public async Task ComposeAsync_APackLanguage_RendersTheSentenceOfItsTranslationsJson(string configured, string pack)
    {
        LoadThePacks();
        SetInstallationLanguage(configured);

        var text = await _sut.ComposeAsync(UnstaffedShift());

        text.ShouldBe(PackSentence(pack, ProactiveMessageI18nKeys.UnstaffedShift, "16.08.2026", "1"));
        text.ShouldNotStartWith("A shift on");
        text.ShouldNotContain("{{");
    }

    [Test]
    public async Task ComposeAsync_APackLanguageWhoseTranslationsWereNeverLoaded_IsEnglish()
    {
        SetInstallationLanguage(Japanese);

        var text = await _sut.ComposeAsync(UnstaffedShift());

        text.ShouldStartWith("A shift on");
    }

    [Test]
    public async Task ComposeAsync_ALoadedPackThatLacksTheKey_FallsBackToEnglishWithAWarning()
    {
        MessengerProactiveTexts.Configure(
            PackWithoutTheKey, new Dictionary<string, string> { [ProactiveMessageI18nKeys.DailyDigest] = "digest" });
        SetInstallationLanguage(PackWithoutTheKey);

        var text = await _sut.ComposeAsync(UnstaffedShift());

        text.ShouldStartWith("A shift on");
        _logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains(ProactiveMessageI18nKeys.UnstaffedShift, StringComparison.Ordinal)
            && entry.Message.Contains(PackWithoutTheKey, StringComparison.Ordinal));
    }

    [Test]
    public async Task ComposeAsync_AValueThatLooksLikeAPlaceholder_IsNotExpandedTwice()
    {
        SetInstallationLanguage("en");
        var hostile = new CatalogueEvent(
            ProactiveMessageI18nKeys.OrderImportFailed,
            new Dictionary<string, string> { ["file"] = "{{reason}}", ["reason"] = "boom" });

        var text = await _sut.ComposeAsync(hostile);

        text.ShouldBe("The ERP import of file {{reason}} failed: boom");
    }

    [Test]
    public async Task ComposeAsync_TheDailyDigest_FillsAllFiveCounters()
    {
        SetInstallationLanguage("en");
        var digest = new CatalogueEvent(
            ProactiveMessageI18nKeys.DailyDigest,
            new Dictionary<string, string>
            {
                ["totalCount"] = CounterValue,
                ["highCount"] = CounterValue,
                ["mediumCount"] = CounterValue,
                ["lowCount"] = CounterValue,
                ["newCount"] = CounterValue
            });

        var text = await _sut.ComposeAsync(digest);

        text.ShouldNotContain("{{");
    }

    [Test]
    public async Task ComposeAsync_NeverLeavesTheRawKeyInACataloguedMessage()
    {
        SetInstallationLanguage("de");

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Does.Not.Contain(ProactiveMessageI18nKeys.UnstaffedShift));
        Assert.That(text, Does.Not.Contain(ProactiveMessageMarkers.I18nPrefix));
    }

    [Test]
    public async Task ComposeAsync_NoLanguageSetting_FallsBackToEnglish()
    {
        SetInstallationLanguage(null);

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Is.EqualTo("A shift on 16.08.2026 (in 1 day(s)) is still unstaffed."));
    }

    [Test]
    public async Task ComposeAsync_UnsupportedLanguageSetting_FallsBackToEnglish()
    {
        SetInstallationLanguage("kl");

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Does.StartWith("A shift on"));
    }

    [Test]
    public async Task ComposeAsync_SettingsLookupThrows_StillProducesAMessageAndLogsAWarning()
    {
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, string>>(_ => throw new InvalidOperationException("database unreachable"));

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Does.StartWith("A shift on"));
        Assert.That(_resolverLogger.Entries.Any(e => e.Level == LogLevel.Warning), Is.True,
            "A message that silently went out in the wrong language must leave a trace.");
    }

    [Test]
    public async Task ComposeAsync_MissingParameter_LeavesThePlaceholderVisible()
    {
        SetInstallationLanguage("de");
        var incomplete = new CatalogueEvent(
            ProactiveMessageI18nKeys.UnstaffedShift,
            new Dictionary<string, string> { ["date"] = "16.08.2026" });

        var text = await _sut.ComposeAsync(incomplete);

        Assert.That(text, Does.Contain("{{days}}"),
            "Dropping the placeholder would produce a complete sentence asserting a fact nobody supplied.");
    }

    [Test]
    public async Task ComposeAsync_KeyOutsideTheCatalogue_DegradesToKeyAndValues()
    {
        SetInstallationLanguage("de");
        var uncatalogued = new CatalogueEvent(
            ProactiveMessageI18nKeys.MuteSuggestion,
            new Dictionary<string, string> { ["kind"] = "unstaffed_shift" });

        var text = await _sut.ComposeAsync(uncatalogued);

        Assert.That(text, Does.StartWith(ProactiveMessageI18nKeys.MuteSuggestion));
        Assert.That(text, Does.Contain("kind: unstaffed_shift"));
        Assert.That(text, Does.Not.Contain(ProactiveMessageMarkers.I18nPrefix));
    }

    [Test]
    public async Task ComposeAsync_PlainTextSummary_IsPassedThroughUnchanged()
    {
        SetInstallationLanguage("de");
        var plain = new PlainSummaryEvent("Something happened.");

        var text = await _sut.ComposeAsync(plain);

        Assert.That(text, Is.EqualTo("Something happened."));
    }

    private sealed record PlainSummaryEvent(string Summary) : IAgentTriggerEvent
    {
        public string Kind => AgentTriggerKinds.UnstaffedShift;
        public string Severity => AgentTriggerSeverity.High;
        public IReadOnlyDictionary<string, object?> Payload => new Dictionary<string, object?>();
    }
}
