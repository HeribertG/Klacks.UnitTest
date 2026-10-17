// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the per-account intro tour (every key under assistant-chat.onboarding.intro.) and the shift
/// holiday options FT/+FT: every key of en.json must exist non-empty, with the same placeholders, in all four core
/// catalogues of Klacks.Ui and in every language plugin. Inconclusive when Klacks.Ui is not checked out next to
/// Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class IntroTourAndHolidayOptionsI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string IntroPrefix = "assistant-chat.onboarding.intro.";

    private static readonly string[] HolidayOptionKeys =
    [
        "holiday",
        "weekday-or-holiday",
        "shift.container-template.weekday.holiday",
        "shift.container-template.weekday.holiday-plus",
        "shift.container-template.tooltip.holiday-filter",
        "shift.container-template.tooltip.holiday-plus-filter",
    ];

    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const int ExpectedPluginCatalogues = 21;
    private const int MinimumIntroKeys = 20;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesTheIntroAndHolidayKeys()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var source = SourceTexts(catalogueDirectory);
        source.Keys.Count(k => k.StartsWith(IntroPrefix, StringComparison.Ordinal)).ShouldBeGreaterThanOrEqualTo(MinimumIntroKeys);
        foreach (var key in HolidayOptionKeys)
        {
            source.ShouldContainKey(key);
        }
    }

    [Test]
    public void EveryKey_ExistsWithTheSamePlaceholdersInAllCatalogues()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        var pluginDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        if (catalogueDirectory == null || pluginDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var source = SourceTexts(catalogueDirectory);
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
            foreach (var (key, english) in source)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{catalogue.Name}: missing {key}");
                    continue;
                }

                if (!Placeholders(text).SetEquals(Placeholders(english)))
                {
                    problems.Add($"{catalogue.Name}: placeholders differ in {key}");
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    private static Dictionary<string, string> SourceTexts(string catalogueDirectory)
    {
        var english = ReadFlat(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension));
        return english
            .Where(entry => entry.Key.StartsWith(IntroPrefix, StringComparison.Ordinal) || HolidayOptionKeys.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
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
