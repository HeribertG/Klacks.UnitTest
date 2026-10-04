// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that the texts explaining contract-first surcharge rates ("empty = standard") and the tri-state
/// shift-work flag on contracts and scheduling rules exist and are non-empty in all four core catalogues of
/// Klacks.Ui and in every language plugin (owner rule: all 25 languages, no English fallback). Follows
/// ClientImportI18nGateTests: the Klacks.Ui half reports inconclusive when that repository is not checked
/// out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ContractStandardRatesI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] Keys =
    [
        "setting.contract.rate-standard-placeholder",
        "setting.contract.rates-standard-hint",
        "setting.contract.performsShiftWork-unset",
        "setting.contract.performsShiftWork-enabled",
        "setting.contract.performsShiftWork-disabled",
        "setting.schedulingRule.shiftWork-unset",
        "setting.schedulingRule.shiftWork-enabled",
        "setting.schedulingRule.shiftWork-disabled",
    ];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string JsonExtension = ".json";
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void EveryText_ExistsInAllFourCoreCatalogues()
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
    public void EveryText_ExistsInEveryLanguagePlugin()
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

    private static IEnumerable<string> ProblemsIn(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var key in Keys)
        {
            if (!document.RootElement.TryGetProperty(key, out var element)
                || string.IsNullOrWhiteSpace(element.GetString()))
            {
                yield return $"{path}: '{key}' is missing or empty";
            }
        }
    }
}
