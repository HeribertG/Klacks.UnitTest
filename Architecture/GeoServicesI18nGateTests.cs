// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every 'geoServices.' text of the geo services UI (map/search/routing notices, route PDF hints,
/// read-only admin status card) that en.json carries exists and is non-empty in all four core catalogues of
/// Klacks.Ui and in every language plugin (owner rule: all 25 languages, no English fallback), that each
/// translation interpolates exactly the same {{placeholders}} as the English source, and that the tile-provider
/// texts of the removed map style select and the retired geo texts (routing notice, its component variant was
/// removed) are gone from every language plugin. Retired keys are excluded from the en.json source, so the gate does
/// not depend on the order in which the UI catalogues drop them. Same structure as the other i18n gates: the
/// Klacks.Ui half reports inconclusive when that repository is not checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GeoServicesI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] RetiredKeys = ["geoServices.notice.routingDisabled"];

    private const string KeyPrefix = "geoServices.";
    private const string RemovedTileKeyPrefix = "dashboard.locations.tile.";
    private const string RemovedMapStyleKey = "dashboard.locations.mapStyle";
    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesTheGeoServicesKeys()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        SourceTexts(catalogueDirectory).ShouldNotBeEmpty();
    }

    [Test]
    public void EveryGeoServicesText_ExistsInAllFourCoreCatalogues()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var source = SourceTexts(catalogueDirectory);
        var problems = CoreLanguages
            .SelectMany(language => ProblemsIn(Path.Combine(catalogueDirectory, language + JsonExtension), source))
            .ToList();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryGeoServicesText_ExistsInEveryLanguagePlugin()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var catalogues = PluginCatalogues();
        catalogues.Count.ShouldBe(ExpectedPluginCatalogues, "Another number of language plugins ships a translations.json.");

        var source = SourceTexts(catalogueDirectory);
        var problems = catalogues.SelectMany(path => ProblemsIn(path, source)).ToList();
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void RemovedMapStyleTexts_AreGoneFromEveryLanguagePlugin()
    {
        var leftovers = PluginCatalogues()
            .SelectMany(path => KeysOf(path)
                .Where(key => key.StartsWith(RemovedTileKeyPrefix, StringComparison.Ordinal)
                              || string.Equals(key, RemovedMapStyleKey, StringComparison.Ordinal))
                .Select(key => $"{path}: '{key}' belongs to the removed map style select"))
            .ToList();

        leftovers.ShouldBeEmpty(string.Join(Environment.NewLine, leftovers));
    }

    [Test]
    public void RetiredGeoServicesTexts_AreGoneFromEveryLanguagePlugin()
    {
        var leftovers = PluginCatalogues()
            .SelectMany(path => KeysOf(path)
                .Where(key => RetiredKeys.Contains(key, StringComparer.Ordinal))
                .Select(key => $"{path}: '{key}' is retired and must be removed"))
            .ToList();

        leftovers.ShouldBeEmpty(string.Join(Environment.NewLine, leftovers));
    }

    private static List<string> PluginCatalogues()
    {
        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        languagesDirectory.ShouldNotBeNull($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");

        return Directory.GetDirectories(languagesDirectory)
            .Select(directory => Path.Combine(directory, TranslationsFileName))
            .Where(File.Exists)
            .ToList();
    }

    private static List<string> KeysOf(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
    }

    private static IReadOnlyDictionary<string, string> SourceTexts(string catalogueDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension)));
        return document.RootElement.EnumerateObject()
            .Where(property => property.Name.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .Where(property => !RetiredKeys.Contains(property.Name, StringComparer.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ProblemsIn(string path, IReadOnlyDictionary<string, string> source)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var (key, english) in source)
        {
            if (!document.RootElement.TryGetProperty(key, out var element)
                || string.IsNullOrWhiteSpace(element.GetString()))
            {
                yield return $"{path}: '{key}' is missing or empty";
                continue;
            }

            var expected = PlaceholdersOf(english);
            var actual = PlaceholdersOf(element.GetString()!);
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            {
                yield return $"{path}: '{key}' has placeholders [{string.Join(", ", actual)}] but en.json has [{string.Join(", ", expected)}]";
            }
        }
    }

    private static List<string> PlaceholdersOf(string text) =>
        Placeholder.Matches(text)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static string? FindDirectory(string relativePath)
    {
        return RepositoryRootLocator.FindDirectory(relativePath);
    }
}
