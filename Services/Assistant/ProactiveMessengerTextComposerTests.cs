// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProactiveMessengerTextComposer — verifies that a messenger message carries a
/// readable sentence in the installation language instead of the raw i18n key, that the language
/// comes from the DEFAULT_LANGUAGE setting with a safe fallback, that an unsupported or unreadable
/// setting never throws inside the dispatch loop, and that a key outside the small server-side
/// catalogue degrades to the key-plus-values form rather than to nothing.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ProactiveMessengerTextComposerTests
{
    private ISettingsReader _settingsReader = null!;
    private RecordingLogger<ProactiveMessengerTextComposer> _logger = null!;
    private ProactiveMessengerTextComposer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _logger = new RecordingLogger<ProactiveMessengerTextComposer>();
        _sut = new ProactiveMessengerTextComposer(_settingsReader, _logger);
    }

    private void SetInstallationLanguage(string? language) =>
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage)
            .Returns(language == null ? null : new SettingsEntity { Type = SettingKeys.DefaultLanguage, Value = language });

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
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage)
            .Returns<SettingsEntity?>(_ => throw new InvalidOperationException("database unreachable"));

        var text = await _sut.ComposeAsync(UnstaffedShift());

        Assert.That(text, Does.StartWith("A shift on"));
        Assert.That(_logger.Entries.Any(e => e.Level == LogLevel.Warning), Is.True,
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
