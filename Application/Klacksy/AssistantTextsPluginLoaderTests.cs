// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AssistantTextsPluginLoader on a throw-away Plugins/Languages tree: a pack directory
/// without assistant-texts.json is reported through onMissingFile (its language would otherwise resolve to
/// English without any warning), a core-language directory is never reported, a pack that ships the file is
/// loaded and not reported, and an unreadable file goes to onError instead. The same tree checks the other two
/// catalogues the loader feeds: assistant-texts.json also configures EscalationHandoffTexts, and the
/// assistant.proactive.* keys of translations.json (and only those) configure MessengerProactiveTexts, also
/// for a pack that ships no assistant-texts.json.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Klacksy;

[TestFixture]
public class AssistantTextsPluginLoaderTests
{
    private const string PackWithoutFile = "qa-nofile";
    private const string PackWithFile = "qa-file";
    private const string PackWithBrokenFile = "qa-broken";
    private const string CoreLanguageDirectory = "de";
    private const string SampleText = "Sample notice";
    private const string OtherTranslationKey = "SOME_UI_LABEL";

    private string _baseDirectory = null!;

    [SetUp]
    public void CreateTree()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), "klacks-assistant-texts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(LanguagesRoot());
    }

    [TearDown]
    public void DeleteTree()
    {
        ClarificationTexts.Reset();
        GracefulCorrectionTexts.Reset();
        EscalationHandoffTexts.Reset();
        MessengerProactiveTexts.Reset();

        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private string LanguagesRoot() => Path.Combine(_baseDirectory, LanguagePluginConstants.PluginDirectory);

    private void CreatePack(string code, string? assistantTextsJson, string? translationsJson = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(LanguagesRoot(), code));
        if (assistantTextsJson != null)
        {
            File.WriteAllText(Path.Combine(directory.FullName, LanguagePluginConstants.AssistantTextsFileName), assistantTextsJson);
        }

        if (translationsJson != null)
        {
            File.WriteAllText(Path.Combine(directory.FullName, LanguagePluginConstants.TranslationsFileName), translationsJson);
        }
    }

    [Test]
    public void APackDirectoryWithoutTheFile_IsReported_AndItsLanguageStaysUnknown()
    {
        CreatePack(PackWithoutFile, null);
        var missing = new List<string>();

        AssistantTextsPluginLoader.Load(_baseDirectory, onMissingFile: missing.Add);

        missing.ShouldBe([PackWithoutFile]);
        ClarificationTexts.TryGetText(ClarificationTextKeys.StatusUnknown, PackWithoutFile, out var text).ShouldBeTrue();
        text.ShouldBe(ClarificationTexts.English(ClarificationTextKeys.StatusUnknown));
    }

    [Test]
    public void ACoreLanguageDirectoryWithoutTheFile_IsNotReported()
    {
        CreatePack(CoreLanguageDirectory, null);
        var missing = new List<string>();

        AssistantTextsPluginLoader.Load(_baseDirectory, onMissingFile: missing.Add);

        missing.ShouldBeEmpty();
    }

    [Test]
    public void APackWithTheFile_IsLoaded_AndNotReported()
    {
        CreatePack(PackWithFile, $"{{\"{ClarificationTextKeys.StatusUnknown}\":\"{SampleText}\"}}");
        var missing = new List<string>();

        AssistantTextsPluginLoader.Load(_baseDirectory, onMissingFile: missing.Add);

        missing.ShouldBeEmpty();
        ClarificationTexts.TryGetText(ClarificationTextKeys.StatusUnknown, PackWithFile, out var text).ShouldBeTrue();
        text.ShouldBe(SampleText);
    }

    [Test]
    public void AnUnreadableFile_GoesToOnError_NotToOnMissingFile()
    {
        CreatePack(PackWithBrokenFile, "{ not json");
        var errors = new List<string>();
        var missing = new List<string>();

        AssistantTextsPluginLoader.Load(_baseDirectory, (file, _) => errors.Add(file), missing.Add);

        errors.Count.ShouldBe(1);
        missing.ShouldBeEmpty();
    }

    [Test]
    public void WithoutACallback_AMissingFileIsSkippedQuietly()
    {
        CreatePack(PackWithoutFile, null);

        Should.NotThrow(() => AssistantTextsPluginLoader.Load(_baseDirectory));
    }

    [Test]
    public void APackWithTheFile_ConfiguresTheEscalationHandoffCatalogueToo()
    {
        CreatePack(PackWithFile, $"{{\"{EscalationHandoffTexts.HandoffQuietNote}\":\"{SampleText}\"}}");

        AssistantTextsPluginLoader.Load(_baseDirectory);

        EscalationHandoffTexts.TryGetText(EscalationHandoffTexts.HandoffQuietNote, PackWithFile, out var text).ShouldBeTrue();
        text.ShouldBe(SampleText);
    }

    [Test]
    public void TheProactiveKeysOfATranslationsJson_ConfigureTheMessengerCatalogue_AndNothingElse()
    {
        var translations = $"{{\"{ProactiveMessageI18nKeys.UnstaffedShift}\":\"{SampleText}\",\"{OtherTranslationKey}\":\"x\"}}";
        CreatePack(PackWithFile, null, translations);

        AssistantTextsPluginLoader.Load(_baseDirectory, onMissingFile: _ => { });

        MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.UnstaffedShift, PackWithFile, out var text).ShouldBeTrue();
        text.ShouldBe(SampleText);
        MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.DailyDigest, PackWithFile, out _).ShouldBeFalse();
    }

    [Test]
    public void APackWithoutAnyProactiveKey_StaysAnUnknownLanguage_AndResolvesToEnglish()
    {
        CreatePack(PackWithFile, null, $"{{\"{OtherTranslationKey}\":\"x\"}}");

        AssistantTextsPluginLoader.Load(_baseDirectory, onMissingFile: _ => { });

        MessengerProactiveTexts.TryGetText(ProactiveMessageI18nKeys.UnstaffedShift, PackWithFile, out var text).ShouldBeTrue();
        text.ShouldBe(MessengerProactiveTexts.EnglishOf(ProactiveMessageI18nKeys.UnstaffedShift));
    }

    [Test]
    public void AnUnreadableTranslationsJson_GoesToOnError()
    {
        CreatePack(PackWithBrokenFile, "{}", "{ not json");
        var errors = new List<string>();

        AssistantTextsPluginLoader.Load(_baseDirectory, (file, _) => errors.Add(file));

        errors.ShouldBe([Path.Combine(LanguagesRoot(), PackWithBrokenFile, LanguagePluginConstants.TranslationsFileName)]);
    }
}
