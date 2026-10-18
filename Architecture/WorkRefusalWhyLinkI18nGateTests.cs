// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the "Why? Ask Klacksy" link in the toast of a refused single work booking (T5 part b, keys
/// schedule.workConflict.askKlacksy, schedule.workConflict.whyTriggerPhrase and schedule.workConflict.clientWithNumber):
/// all exist non-empty in the four core catalogues of Klacks.Ui and in all 21 language plugins, the question carries
/// exactly the placeholders client, date and shift, the person label (name plus employee number) exactly name and
/// number, and no text is the English one in another language - the question is sent as the user's own chat
/// message, so an English fallback would make Klacksy answer in the wrong language. Inconclusive when Klacks.Ui is not
/// checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class WorkRefusalWhyLinkI18nGateTests
{
    private const string AskKlacksyKey = "schedule.workConflict.askKlacksy";
    private const string TriggerPhraseKey = "schedule.workConflict.whyTriggerPhrase";
    private const string ClientWithNumberKey = "schedule.workConflict.clientWithNumber";

    private static readonly Dictionary<string, string[]> KeyPlaceholders = new()
    {
        [AskKlacksyKey] = [],
        [TriggerPhraseKey] = ["client", "date", "shift"],
        [ClientWithNumberKey] = ["name", "number"],
    };

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
    public void AllKeys_ExistTranslated_WithTheirPlaceholders_InAllTwentyFiveCatalogues()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        var pluginDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        if (catalogueDirectory == null || pluginDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var english = ReadFlat(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension));
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
            foreach (var (key, placeholders) in KeyPlaceholders)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{catalogue.Name}: missing {key}");
                    continue;
                }

                if (!Placeholders(text).SetEquals(placeholders))
                {
                    problems.Add($"{catalogue.Name}: {key} has [{string.Join(", ", Placeholders(text))}], expected [{string.Join(", ", placeholders)}]");
                }

                if (catalogue.Name != SourceLanguage
                    && english.TryGetValue(key, out var source)
                    && string.Equals(text.Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase))
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
