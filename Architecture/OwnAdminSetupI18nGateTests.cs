// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the own-admin setup page (password rules, confirmation, inline errors) and the translated
/// reasons of a rejected registration: every key must exist and be non-empty, with the same placeholders as
/// en.json, in all four core catalogues of Klacks.Ui and in every language plugin (owner rule: all 25 languages, no
/// English fallback). The retired "setup.userName" must be gone everywhere, because the server now generates the
/// user name. Same structure as the other i18n gates: inconclusive when Klacks.Ui is not checked out next to
/// Klacks.Api.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class OwnAdminSetupI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] Keys =
    [
        "setup.passwordConfirm",
        "setup.passwordRules",
        "setup.rule.length",
        "setup.rule.digit",
        "setup.rule.nonAlphanumeric",
        "setup.rule.uppercase",
        "setup.rule.lowercase",
        "setup.error.required",
        "setup.error.passwordMismatch",
        "setup.error.alreadyCompleted",
        "setup.error.server",
        "accountRegistration.reason.PasswordTooShort",
        "accountRegistration.reason.PasswordRequiresDigit",
        "accountRegistration.reason.PasswordRequiresNonAlphanumeric",
        "accountRegistration.reason.PasswordRequiresUpper",
        "accountRegistration.reason.PasswordRequiresLower",
        "accountRegistration.reason.PasswordRequiresUniqueChars",
        "accountRegistration.reason.DuplicateEmail",
        "accountRegistration.reason.DuplicateUserName",
        "accountRegistration.reason.InvalidEmail",
        "accountRegistration.reason.InvalidUserName",
        "accountRegistration.reason.unknown",
    ];

    private const string RetiredKey = "setup.userName";
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
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        SourceTexts(catalogueDirectory).Keys.ShouldBe(Keys, ignoreOrder: true);
    }

    [Test]
    public void EveryKey_ExistsInAllCataloguesAndTheRetiredKeyIsGone()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var languagesDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        languagesDirectory.ShouldNotBeNull($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");

        var pluginCatalogues = Directory.GetDirectories(languagesDirectory)
            .Select(directory => Path.Combine(directory, TranslationsFileName))
            .Where(File.Exists)
            .ToList();
        pluginCatalogues.Count.ShouldBe(ExpectedPluginCatalogues, "Another number of language plugins ships a translations.json.");

        var catalogues = CoreLanguages
            .Select(language => Path.Combine(catalogueDirectory, language + JsonExtension))
            .Concat(pluginCatalogues)
            .ToList();

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
        if (document.RootElement.TryGetProperty(RetiredKey, out _))
        {
            yield return $"{path}: retired key '{RetiredKey}' is still present";
        }

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
}
