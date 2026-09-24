// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AssistantTextsPluginLoader on a throw-away Plugins/Languages tree: a pack directory
/// without assistant-texts.json is reported through onMissingFile (its language would otherwise resolve to
/// English without any warning), a core-language directory is never reported, a pack that ships the file is
/// loaded and not reported, and an unreadable file goes to onError instead.
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

        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }

    private string LanguagesRoot() => Path.Combine(_baseDirectory, LanguagePluginConstants.PluginDirectory);

    private void CreatePack(string code, string? assistantTextsJson)
    {
        var directory = Directory.CreateDirectory(Path.Combine(LanguagesRoot(), code));
        if (assistantTextsJson != null)
        {
            File.WriteAllText(Path.Combine(directory.FullName, LanguagePluginConstants.AssistantTextsFileName), assistantTextsJson);
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
}
