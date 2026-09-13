// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that the whole correction menu of the Klacksy chat - the eight entries of its first level and
/// the four sentences of the expected-skill panel - can be rendered in every language the menu is offered
/// in: the four core catalogues of Klacks.Ui and every language plugin that already carries the menu.
/// Follows EvalRegressionI18nGateTests: the Klacks.Ui files live in a different repository and the backend
/// CI job does not check them out, so that half reports itself inconclusive when they are unreachable
/// instead of failing a job that has nothing to do with the frontend. The plugin half has no such excuse -
/// those files ship inside Klacks.Api. A missing key does not fall back to another language here:
/// ngx-translate renders the raw key, so the user reads "assistant-chat.correction.expected-skill-title"
/// where the question should be.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class CorrectionMenuI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    /// <summary>
    /// Every key the correction menu renders: the eight entries of its first level, followed by the four
    /// sentences of the expected-skill panel that the skill-learning signals added underneath them.
    /// </summary>
    private static readonly string[] CorrectionMenuKeys =
    [
        "assistant-chat.correction.flag",
        "assistant-chat.correction.thanks",
        "assistant-chat.correction.not-helpful",
        "assistant-chat.correction.wrong-skill",
        "assistant-chat.correction.wrong-param",
        "assistant-chat.correction.none-needed",
        "assistant-chat.correction.comment-placeholder",
        "assistant-chat.correction.comment-send",
        "assistant-chat.correction.expected-skill-title",
        "assistant-chat.correction.expected-skill-loading",
        "assistant-chat.correction.expected-skill-placeholder",
        "assistant-chat.correction.expected-skill-unknown",
    ];

    /// <summary>
    /// The key that decides whether a language plugin carries the correction menu at all. It is itself one
    /// of the guarded keys, so a catalogue that drops the whole block leaves the scan and fails the count
    /// below instead of passing unnoticed.
    /// </summary>
    private const string AnchorKey = "assistant-chat.correction.comment-send";

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";

    /// <summary>
    /// How many language plugins carry the correction menu today. Pinned rather than compared against zero
    /// so that a catalogue silently dropping out of the scan is a failure: without it, a rename of the
    /// anchor key or of the folder layout would leave the gate green while checking nothing. Raise it
    /// deliberately together with a new language pack.
    /// </summary>
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void EveryCorrectionMenuKeyExistsInAllFourCoreCatalogues()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        foreach (var language in CoreLanguages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            File.Exists(path).ShouldBeTrue($"Missing frontend catalogue '{path}'.");
            AssertEveryKey(path);
        }
    }

    [Test]
    public void EveryPluginCatalogueCarryingTheCorrectionMenuAlsoCarriesEveryKey()
    {
        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        if (languagesDirectory == null)
        {
            Assert.Inconclusive($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");
            return;
        }

        var checkedCatalogues = 0;

        foreach (var directory in Directory.GetDirectories(languagesDirectory))
        {
            var path = Path.Combine(directory, TranslationsFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(AnchorKey, out _))
            {
                continue;
            }

            AssertEveryKey(path);
            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(
            ExpectedPluginCatalogues,
            "Another number of language plugins carries the correction menu than this gate scans.");
    }

    private static void AssertEveryKey(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var key in CorrectionMenuKeys)
        {
            document.RootElement
                .TryGetProperty(key, out var value)
                .ShouldBeTrue($"'{key}' is missing from '{path}'.");
            value.GetString().ShouldNotBeNullOrWhiteSpace($"'{key}' is empty in '{path}'.");
        }
    }

    private static string? FindDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
