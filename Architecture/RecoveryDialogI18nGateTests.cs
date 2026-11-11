// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the texts of the absence cover dialog that name how a replacement was found: the marks for a borrowed person
/// and for a contract day off and the escalation tiers 0, 1, 2, 3, 5 and 6 exist and are non-empty in all four core
/// catalogues of Klacks.Ui and in every language plugin (owner rule: all 25 languages, no English fallback). Inside one
/// catalogue the tier texts differ from each other and carry no digit: the texts once named a number of swaps for the
/// cross-group tiers, which was wrong. Follows StandInI18nGateTests: the Klacks.Ui half reports inconclusive when that
/// repository is not checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class RecoveryDialogI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] MarkKeys =
    [
        "recovery.dialog.borrowed",
        "recovery.dialog.contractDayOff",
    ];

    private static readonly string[] TierKeys =
    [
        "recovery.dialog.tier.0",
        "recovery.dialog.tier.1",
        "recovery.dialog.tier.2",
        "recovery.dialog.tier.3",
        "recovery.dialog.tier.5",
        "recovery.dialog.tier.6",
    ];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void EveryText_ExistsInAllFourCoreCatalogues_AndTheTiersAreDistinctWithoutDigits()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var problems = CoreLanguages
            .SelectMany(language => ProblemsIn(Path.Combine(catalogueDirectory, language + JsonExtension)))
            .ToList();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryText_ExistsInEveryLanguagePlugin_AndTheTiersAreDistinctWithoutDigits()
    {
        var languagesDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        languagesDirectory.ShouldNotBeNull($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");

        var catalogues = Directory.GetDirectories(languagesDirectory)
            .Select(directory => Path.Combine(directory, TranslationsFileName))
            .Where(File.Exists)
            .ToList();

        catalogues.Count.ShouldBe(ExpectedPluginCatalogues, "Another number of language plugins ships a translations.json.");

        var problems = catalogues.SelectMany(ProblemsIn).ToList();
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ATierTextWithADigit_OrTwoEqualTierTexts_AreReported()
    {
        // Arrange
        var texts = TierKeys.ToDictionary(key => key, key => $"text of {key[^1]}");
        texts[TierKeys[1]] = "same text";
        texts[TierKeys[2]] = "same text";

        // Act
        var problems = TierProblems("catalogue", texts).ToList();

        // Assert
        problems.Count(problem => problem.Contains("digit")).ShouldBe(TierKeys.Length - 2);
        problems.Count(problem => problem.Contains("same text")).ShouldBe(1);
    }

    private static IEnumerable<string> ProblemsIn(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var tierTexts = new Dictionary<string, string>();
        foreach (var key in MarkKeys.Concat(TierKeys))
        {
            var text = document.RootElement.TryGetProperty(key, out var element) ? element.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                yield return $"{path}: '{key}' is missing or empty";
            }
            else if (Array.IndexOf(TierKeys, key) >= 0)
            {
                tierTexts[key] = text;
            }
        }

        foreach (var problem in TierProblems(path, tierTexts))
        {
            yield return problem;
        }
    }

    private static IEnumerable<string> TierProblems(string path, IReadOnlyDictionary<string, string> tierTexts)
    {
        foreach (var (key, text) in tierTexts)
        {
            if (text.Any(char.IsDigit))
            {
                yield return $"{path}: '{key}' carries a digit: {text}";
            }
        }

        foreach (var sameText in tierTexts.GroupBy(entry => entry.Value.Trim()).Where(group => group.Count() > 1))
        {
            yield return $"{path}: {string.Join(", ", sameText.Select(entry => entry.Key))} share the same text: {sameText.Key}";
        }
    }
}
