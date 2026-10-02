// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that the collective unstaffed-shift sentence (Paket D) exists in every catalogue the message can be
/// rendered from, with all five placeholders the event fills: the four core catalogues in Klacks.Ui and every
/// language plugin that already carries the per-shift key. Follows WelcomeFocusI18nGateTests: the Klacks.Ui files live in a different repository
/// and the backend CI job does not check them out, so that half reports itself inconclusive when they are
/// unreachable instead of failing a job that has nothing to do with the frontend. The plugin half has no
/// such excuse - those files ship inside Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UnstaffedShiftSummaryI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string PlaceholderPrefix = "{{";
    private const string PlaceholderSuffix = "}}";
    private static readonly string[] PlaceholderNames = ["group", "count", "days", "from", "until"];

    /// <summary>
    /// How many language plugins carry the per-shift key today. Pinned rather than compared against zero
    /// so that a catalogue silently dropping out of the scan is a failure: without it, a rename of the key
    /// or of the folder layout would leave the gate green while checking nothing. Raise it deliberately
    /// together with a new language pack.
    /// </summary>
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void TheSummaryKeyExistsInAllFourCoreCatalogues()
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
            AssertSentence(path);
        }
    }

    [Test]
    public void EveryPluginCatalogueCarryingThePerShiftKeyAlsoCarriesTheSummaryKey()
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
            if (!document.RootElement.TryGetProperty(ProactiveMessageI18nKeys.UnstaffedShift, out _))
            {
                continue;
            }

            AssertSentence(path);
            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(
            ExpectedPluginCatalogues,
            "Another number of language plugins carries the per-shift key than this gate scans.");
    }

    private static void AssertSentence(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement
            .TryGetProperty(ProactiveMessageI18nKeys.UnstaffedShiftSummary, out var value)
            .ShouldBeTrue($"'{ProactiveMessageI18nKeys.UnstaffedShiftSummary}' is missing from '{path}'.");

        var sentence = value.GetString();
        sentence.ShouldNotBeNullOrEmpty($"The summary sentence is empty in '{path}'.");

        foreach (var name in PlaceholderNames)
        {
            var placeholder = PlaceholderPrefix + name + PlaceholderSuffix;
            sentence.ShouldContain(placeholder, customMessage: $"'{placeholder}' is missing from '{path}'.");
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
