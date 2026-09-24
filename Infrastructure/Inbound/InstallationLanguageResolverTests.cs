// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InstallationLanguageResolver: DEFAULT_LANGUAGE is returned as configured (trimmed, with
/// the casing of a pack code such as zh-CN and regional tags untouched, and NOT filtered against
/// LanguageConfig.SupportedLanguages, which never lists the installed packs at runtime), and a missing,
/// blank or unreadable setting falls back to English without an exception.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class InstallationLanguageResolverTests
{
    private ISettingsReader _settingsReader = null!;
    private InstallationLanguageResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _resolver = new InstallationLanguageResolver(_settingsReader, NullLogger<InstallationLanguageResolver>.Instance);
    }

    private void Configure(string? value) =>
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.DefaultLanguage, Value = value! });

    [TestCase("de", "de")]
    [TestCase("zh-CN", "zh-CN")]
    [TestCase("zh-cn", "zh-cn")]
    [TestCase("pt", "pt")]
    [TestCase("de-CH", "de-CH")]
    [TestCase("  fr ", "fr")]
    [TestCase("xx", "xx")]
    public async Task ReturnsTheConfiguredLanguage_AsConfigured(string configured, string expected)
    {
        Configure(configured);

        (await _resolver.ResolveAsync()).ShouldBe(expected);
    }

    [Test]
    public async Task APackLanguage_IsNotFilteredAgainstTheCoreLanguageList()
    {
        LanguageConfig.SupportedLanguages.ShouldNotContain("ja");
        Configure("ja");

        (await _resolver.ResolveAsync()).ShouldBe("ja");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task ABlankSetting_FallsBackToEnglish(string? configured)
    {
        Configure(configured);

        (await _resolver.ResolveAsync()).ShouldBe(LanguageConfig.DefaultLanguageFallback);
    }

    [Test]
    public async Task AMissingSetting_FallsBackToEnglish()
    {
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        (await _resolver.ResolveAsync()).ShouldBe(LanguageConfig.DefaultLanguageFallback);
    }

    [Test]
    public async Task AnUnreadableSetting_FallsBackToEnglish_AndLogsIt()
    {
        var logger = new TestHelpers.RecordingLogger<InstallationLanguageResolver>();
        _resolver = new InstallationLanguageResolver(_settingsReader, logger);
        _settingsReader.GetSetting(SettingKeys.DefaultLanguage).Returns<Klacks.Api.Domain.Models.Settings.Settings?>(
            _ => throw new InvalidOperationException("database down"));

        (await _resolver.ResolveAsync()).ShouldBe(LanguageConfig.DefaultLanguageFallback);
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException);
    }
}
