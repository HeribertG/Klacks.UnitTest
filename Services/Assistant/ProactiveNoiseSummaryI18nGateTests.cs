// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that the three aggregated data-quality sentences exist in every catalogue they can be rendered
/// from: the four core catalogues in Klacks.Ui and every language plugin that already carries the
/// matching per-employee key. Clones TargetHoursDriftSummaryI18nGateTests, including its split: the
/// Klacks.Ui files live in a different repository the backend CI job does not check out, so that half
/// reports itself inconclusive when they are unreachable rather than failing a job that has nothing to
/// do with the frontend. The plugin half has no such excuse - those files ship inside Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class ProactiveNoiseSummaryI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";

    /// <summary>
    /// How many language plugins carry the per-employee keys today. Pinned rather than compared against
    /// zero so that a catalogue silently dropping out of the scan is a failure: without it, a rename of a
    /// key or of the folder layout would leave the gate green while checking nothing. Raise it
    /// deliberately together with a new language pack.
    /// </summary>
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void AllThreeSummarySentencesExistInAllFourCoreCatalogues()
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
            AssertAllSentences(path);
        }
    }

    [Test]
    public void EveryPluginCatalogueCarryingThePerEmployeeKeysAlsoCarriesTheSummaryKeys()
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
            if (!document.RootElement.TryGetProperty(ProactiveMessageI18nKeys.AvailabilityGap, out _))
            {
                continue;
            }

            AssertAllSentences(path);
            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(
            ExpectedPluginCatalogues,
            "Another number of language plugins carries the per-employee keys than this gate scans.");
    }

    /// <summary>
    /// The per-employee keys must SURVIVE the aggregation in every catalogue: inbox rows written before
    /// it still carry them as their ContentKey, and ProactiveReminderService passes an i18n ContentKey
    /// through untouched, so removing one would render an old row as a raw key in the user's language.
    /// </summary>
    [Test]
    public void ThePerEmployeeKeysStayInEveryPluginCatalogue()
    {
        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        if (languagesDirectory == null)
        {
            Assert.Inconclusive($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");
            return;
        }

        string[] perEmployeeKeys =
        [
            ProactiveMessageI18nKeys.AvailabilityGap,
            ProactiveMessageI18nKeys.ClientMissingAddress,
            ProactiveMessageI18nKeys.ClientMissingContact
        ];

        var checkedCatalogues = 0;

        foreach (var directory in Directory.GetDirectories(languagesDirectory))
        {
            var path = Path.Combine(directory, TranslationsFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(ProactiveMessageI18nKeys.AvailabilityGap, out _))
            {
                continue;
            }

            foreach (var key in perEmployeeKeys)
            {
                document.RootElement.TryGetProperty(key, out var value)
                    .ShouldBeTrue($"'{key}' is missing from '{path}'.");
                value.GetString().ShouldNotBeNullOrEmpty($"'{key}' is empty in '{path}'.");
            }

            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(ExpectedPluginCatalogues);
    }

    private static void AssertAllSentences(string path)
    {
        AssertSentence(
            path,
            ProactiveMessageI18nKeys.AvailabilityGapSummary,
            ProactiveNoiseSummaryPlaceholders.AvailabilityGapNames);
        AssertSentence(
            path,
            ProactiveMessageI18nKeys.ClientMissingAddressSummary,
            ProactiveNoiseSummaryPlaceholders.ClientMissingCoreDataNames);
        AssertSentence(
            path,
            ProactiveMessageI18nKeys.ClientMissingContactSummary,
            ProactiveNoiseSummaryPlaceholders.ClientMissingCoreDataNames);
    }

    private static void AssertSentence(string path, string key, IReadOnlyList<string> placeholderNames)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement
            .TryGetProperty(key, out var value)
            .ShouldBeTrue($"'{key}' is missing from '{path}'.");

        var sentence = value.GetString();
        sentence.ShouldNotBeNullOrEmpty($"The sentence for '{key}' is empty in '{path}'.");

        foreach (var name in placeholderNames)
        {
            var placeholder = ProactiveNoiseSummaryPlaceholders.Placeholder(name);
            sentence.ShouldContain(
                placeholder, customMessage: $"'{placeholder}' is missing from '{key}' in '{path}'.");
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
