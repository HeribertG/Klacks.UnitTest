// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the stale-scenario accept dialog (the original plan changed after the scenario was created and
/// accepting overwrites those changes) and of the notice about entries not taken over on locked days: every key must exist and be non-empty, with the same placeholders as en.json, in
/// all four core catalogues of Klacks.Ui and in every language plugin (owner rule: all 25 languages, no English
/// fallback). Same structure as the other i18n gates: the Klacks.Ui half reports inconclusive when that repository is not
/// checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ScenarioStaleAcceptI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] Keys =
    [
        "scenario.accept.staleTitle",
        "scenario.accept.staleMessage",
        "scenario.accept.staleAcceptedScenarios",
        "scenario.accept.staleConfirmButton",
        "scenario.accept.lockedSkipped",
    ];

    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesEveryKey()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        SourceTexts(catalogueDirectory).Keys.ShouldBe(Keys, ignoreOrder: true);
    }

    [Test]
    public void EveryKey_ExistsInAllFourCoreCatalogues()
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
    public void EveryKey_ExistsInEveryLanguagePlugin()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        languagesDirectory.ShouldNotBeNull($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");

        var catalogues = Directory.GetDirectories(languagesDirectory)
            .Select(directory => Path.Combine(directory, TranslationsFileName))
            .Where(File.Exists)
            .ToList();

        catalogues.Count.ShouldBe(ExpectedPluginCatalogues, "Another number of language plugins ships a translations.json.");

        var source = SourceTexts(catalogueDirectory);
        var problems = catalogues.SelectMany(path => ProblemsIn(path, source)).ToList();
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static IReadOnlyDictionary<string, string> SourceTexts(string catalogueDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension)));
        return document.RootElement.EnumerateObject()
            .Where(property => Keys.Contains(property.Name, StringComparer.Ordinal))
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
