// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every text of the directory sign-in approval (the 'settings.identity-provider.accounts.' keys, the tab
/// label, the user administration label for directory-only accounts and the sign-in refusal message) that en.json
/// carries exists and is non-empty in all four core catalogues of Klacks.Ui and in every language plugin (owner rule:
/// all 25 languages, no English fallback), that each translation interpolates exactly the same {{placeholders}} as the
/// English source, and that every machine code the server answers for a refused approval has its own error text. Same
/// structure as the other i18n gates: the Klacks.Ui half reports inconclusive when that repository is not checked out
/// next to Klacks.Api.
/// </summary>

using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class DirectoryAccountsI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] ExactKeys =
    [
        "settings.identity-provider.tab-accounts",
        "setting.user-admin.row.directory-login",
        "AUTH_DIRECTORY_ACCESS_NOT_GRANTED",
        "settings.identity-provider.accounts.hint.email-in-use",
        "settings.identity-provider.accounts.hint.has-roles",
        "settings.identity-provider.warning.ad-requires-ssl",
        "settings.identity-provider.warning.ldap-unencrypted",
    ];

    private const string KeyPrefix = "settings.identity-provider.accounts.";
    private const string ErrorKeyPrefix = "settings.identity-provider.accounts.error.";
    private const string SourceLanguage = "en";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const string PlaceholderPattern = @"\{\{\s*([^{}\s]+)\s*\}\}";
    private const string RemovedLinksExistingKey = "settings.identity-provider.accounts.hint.links-existing";
    private const int ExpectedPluginCatalogues = 21;
    private const int MinimumSourceTexts = 21;

    private static readonly Regex Placeholder = new(PlaceholderPattern, RegexOptions.CultureInvariant);

    [Test]
    public void EnglishSource_CarriesTheDirectoryAccountsKeys()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var source = SourceTexts(catalogueDirectory);

        source.Count.ShouldBeGreaterThanOrEqualTo(MinimumSourceTexts);
        foreach (var key in ExactKeys)
        {
            source.ContainsKey(key).ShouldBeTrue($"en.json has no '{key}'");
        }

        source.ContainsKey(RemovedLinksExistingKey).ShouldBeFalse("e-mail linking was removed; the hint must be gone");
    }

    [Test]
    public void EveryDirectoryAccountsText_ExistsInAllFourCoreCatalogues()
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
    public void EveryDirectoryAccountsText_ExistsInEveryLanguagePlugin()
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
    public void EveryServerErrorCodeOfTheApproval_HasAnErrorTextInTheEnglishSource()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var source = SourceTexts(catalogueDirectory);
        var approvalCodes = typeof(DirectoryAccountErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(code => !string.Equals(code, DirectoryAccountErrorCodes.AccessNotGranted, StringComparison.Ordinal))
            .ToList();

        approvalCodes.ShouldNotBeEmpty();
        var missing = approvalCodes.Where(code => !source.ContainsKey(ErrorKeyPrefix + code)).ToList();
        missing.ShouldBeEmpty($"en.json has no '{ErrorKeyPrefix}<code>' text for: {string.Join(", ", missing)}");
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

    private static IReadOnlyDictionary<string, string> SourceTexts(string catalogueDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(catalogueDirectory, SourceLanguage + JsonExtension)));
        return document.RootElement.EnumerateObject()
            .Where(property => property.Name.StartsWith(KeyPrefix, StringComparison.Ordinal)
                               || ExactKeys.Contains(property.Name, StringComparer.Ordinal))
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
