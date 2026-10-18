// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the FT/+FT explain offer (T5, keys shift.holidayOptions.explainOffer.*): every key exists
/// non-empty, with the same placeholders, in the four core catalogues of Klacks.Ui and in all 21 language plugins, and
/// the two sentences (the offer and the trigger phrase the "yes" chip sends as the user's chat message) are really
/// translated - no English fallback in an installed language. The short chip labels are exempt from the
/// "differs from English" rule: "Later" is the correct Dutch word. Inconclusive when Klacks.Ui is not checked out next
/// to Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UiExplainOfferI18nGateTests
{
    private const string KeyPrefix = "shift.holidayOptions.explainOffer.";
    private const string PromptKey = KeyPrefix + "prompt";
    private const string TriggerPhraseKey = KeyPrefix + "triggerPhrase";

    private static readonly string[] Keys =
    [
        PromptKey,
        KeyPrefix + "accept",
        KeyPrefix + "later",
        KeyPrefix + "decline",
        TriggerPhraseKey
    ];

    private static readonly string[] SentenceKeys = [PromptKey, TriggerPhraseKey];

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EveryKey_ExistsTranslated_WithTheSamePlaceholders_InAllTwentyFiveCatalogues()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        var pluginDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        if (catalogueDirectory == null || pluginDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var english = ReadFlat(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension));
        foreach (var key in Keys)
        {
            english.ShouldContainKey(key);
            english[key].ShouldNotBeNullOrWhiteSpace();
        }

        var catalogues = CoreLanguages
            .Select(language => (Name: language, Path: Path.Combine(catalogueDirectory, language + JsonExtension)))
            .Concat(Directory.GetDirectories(pluginDirectory)
                .Select(directory => (Name: Path.GetFileName(directory), Path: Path.Combine(directory, TranslationsFileName)))
                .Where(entry => File.Exists(entry.Path)))
            .ToList();
        catalogues.Count.ShouldBe(CoreLanguages.Length + ExpectedPluginCatalogues);

        var problems = new List<string>();
        foreach (var catalogue in catalogues)
        {
            var texts = ReadFlat(catalogue.Path);
            foreach (var key in Keys)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{catalogue.Name}: missing {key}");
                    continue;
                }

                if (!Placeholders(text).SetEquals(Placeholders(english[key])))
                {
                    problems.Add($"{catalogue.Name}: placeholders differ in {key}");
                }

                if (catalogue.Name != SourceLanguage
                    && SentenceKeys.Contains(key)
                    && string.Equals(text.Trim(), english[key].Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{catalogue.Name}: {key} is the English text");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static Dictionary<string, string> ReadFlat(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder.Matches(text).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
}
