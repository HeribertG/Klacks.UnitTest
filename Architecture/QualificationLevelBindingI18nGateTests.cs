// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the binding minimum level (qualification checkbox, three-way choice on the shift requirement,
/// detailed error-list and refused-booking texts, replacement hint): every key must exist non-empty in en.json and,
/// with the same placeholders, in all four core catalogues of Klacks.Ui and in every language plugin. Inconclusive
/// when Klacks.Ui is not checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class QualificationLevelBindingI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] Keys =
    [
        "setting.qualifications.modal.level-binding",
        "setting.qualifications.modal.level-binding-label",
        "setting.qualifications.modal.level-binding-hint",
        "shift.edit-shift.qualifications.table.level-binding",
        "shift.edit-shift.qualifications.level-binding.inherit",
        "shift.edit-shift.qualifications.level-binding.inherit-current",
        "shift.edit-shift.qualifications.level-binding.binding",
        "shift.edit-shift.qualifications.level-binding.hint",
        "schedule.error-list.qualification-level-detail",
        "schedule.workConflict.qualificationLevelHeld",
        "recovery.dialog.levelShortfall",
    ];

    private const string DetailKey = "schedule.error-list.qualification-level-detail";
    private const string HeldLevelPlaceholder = "heldLevel";
    private const string MinLevelPlaceholder = "minLevel";
    private const string LevelDeltaPlaceholder = "levelDelta";

    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesEveryKeyAndTheLevelPlaceholders()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
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

        Placeholders(english[DetailKey]).ShouldBe(
            new HashSet<string>([HeldLevelPlaceholder, MinLevelPlaceholder, LevelDeltaPlaceholder]),
            ignoreOrder: true);
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
            foreach (var key in Keys)
            {
                if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{catalogue.Name}: missing {key}");
                    continue;
                }

                if (english.TryGetValue(key, out var source) && !Placeholders(text).SetEquals(Placeholders(source)))
                {
                    problems.Add($"{catalogue.Name}: placeholders differ in {key}");
                }
            }
        }

        problems.ShouldBeEmpty();
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
