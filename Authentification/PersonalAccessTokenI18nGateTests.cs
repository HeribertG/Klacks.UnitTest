// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every 'setting.personal-access-tokens.' text of the token settings card (including the Read/Write
/// access-mode choice) that en.json carries exists and is non-empty in all four core catalogues of Klacks.Ui and
/// in every language plugin (owner rule: all 25 languages, no English fallback), with the same {{placeholders}}
/// as the English source, and that every token manual names both access modes with the exact UI labels of its
/// language. Follows ClientImportI18nGateTests: the Klacks.Ui half reports inconclusive when that repository is
/// not checked out next to the Klacks.Api of the same source tree (a git worktree without Klacks.Ui included).
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class PersonalAccessTokenI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string KeyPrefix = "setting.personal-access-tokens.";
    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const int ExpectedPluginCatalogues = 21;
    private const string CoreManualRelativePath = "Klacks.Ui/src/assets/docs/personal-access-token-manual";
    private const string PluginDocsDirectory = "docs";
    private const string ManualFileName = "personal-access-token-manual.html";
    private const string HtmlExtension = ".html";
    private const string ApiProjectDirectory = "Klacks.Api";

    private static readonly string[] AccessModeLabelKeys =
    [
        "setting.personal-access-tokens.access-mode.read",
        "setting.personal-access-tokens.access-mode.write"
    ];

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesThePersonalAccessTokenKeys()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        SourceTexts(catalogueDirectory).ShouldNotBeEmpty();
    }

    [Test]
    public void EveryPersonalAccessTokenText_ExistsInAllFourCoreCatalogues()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        var source = SourceTexts(catalogueDirectory);
        var problems = CoreLanguages
            .SelectMany(language => ProblemsIn(Path.Combine(catalogueDirectory, language + JsonExtension), source))
            .ToList();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPersonalAccessTokenText_ExistsInEveryLanguagePlugin()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
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

    [Test]
    public void EveryTokenManual_NamesBothAccessModesWithTheUiLabels()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        var coreManualDirectory = FindDirectory(CoreManualRelativePath);
        if (catalogueDirectory == null || coreManualDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        languagesDirectory.ShouldNotBeNull($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");

        var pairs = CoreLanguages
            .Select(language => (
                Catalogue: Path.Combine(catalogueDirectory, language + JsonExtension),
                Manual: Path.Combine(coreManualDirectory, language + HtmlExtension)))
            .Concat(Directory.GetDirectories(languagesDirectory)
                .Where(directory => File.Exists(Path.Combine(directory, TranslationsFileName)))
                .Select(directory => (
                    Catalogue: Path.Combine(directory, TranslationsFileName),
                    Manual: Path.Combine(directory, PluginDocsDirectory, ManualFileName))))
            .ToList();

        pairs.Count.ShouldBe(CoreLanguages.Length + ExpectedPluginCatalogues);

        var problems = new List<string>();
        foreach (var (cataloguePath, manualPath) in pairs)
        {
            if (!File.Exists(manualPath))
            {
                problems.Add($"{manualPath}: manual is missing");
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(cataloguePath));
            var manual = File.ReadAllText(manualPath);
            foreach (var key in AccessModeLabelKeys)
            {
                var label = document.RootElement.TryGetProperty(key, out var element) ? element.GetString() : null;
                if (string.IsNullOrWhiteSpace(label) || !manual.Contains(label, StringComparison.Ordinal))
                {
                    problems.Add($"{manualPath}: does not name the '{key}' label '{label}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static IReadOnlyDictionary<string, string> SourceTexts(string catalogueDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension)));
        return document.RootElement.EnumerateObject()
            .Where(property => property.Name.StartsWith(KeyPrefix, StringComparison.Ordinal))
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

    /// <summary>
    /// Resolves a path relative to the source tree this test was built from: the nearest ancestor of the test
    /// directory that holds Klacks.Api (the repositories sit next to each other there, locally and in CI). The
    /// search deliberately stops at that root, so a git worktree without its own Klacks.Ui reports inconclusive
    /// instead of reading another checkout further up the disk.
    /// </summary>
    private static string? FindDirectory(string relativePath)
    {
        var treeRoot = FindTreeRoot();
        if (treeRoot == null)
        {
            return null;
        }

        var candidate = Path.Combine([treeRoot, .. relativePath.Split('/')]);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindTreeRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
