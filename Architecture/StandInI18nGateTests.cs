// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that the texts around stand-ins (placeholders for people not in the system yet) exist and are non-empty in
/// all four core catalogues of Klacks.Ui and in every language plugin (owner rule: all 25 languages, no English
/// fallback): the type name and the schedule filter, the row header tooltip, the payroll blocker, the period issue,
/// the refused period close and the refused delete, and the two dialogs "create placeholders" and "convert into
/// employee" with their labels, refusals and results and the placeholder's master data card, and the more-actions menu
/// of the schedule toolbar that opens "create placeholders" (its tooltip, the hint without a selected group and the
/// placeholder text of the qualification pull-down). Follows
/// ContractStandardRatesI18nGateTests: the Klacks.Ui half reports inconclusive when that repository is not checked
/// out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class StandInI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] Keys =
    [
        "address.edit-address.membership.type.standIn",
        "address.delete.standInHasWorks",
        "schedule.filter.standIns",
        "schedule.row-header.stand-in.tooltip",
        "periodClosing.payroll.reason.standInAssigned",
        "periodClosing.issues.code.standInWorks",
        "periodClosing.error.standInWorks",
        "standIn.create.title",
        "standIn.create.hint",
        "standIn.create.count",
        "standIn.create.namePattern",
        "standIn.create.namePatternExample",
        "standIn.create.group",
        "standIn.create.validFrom",
        "standIn.create.validUntil",
        "standIn.create.qualifications",
        "standIn.create.qualificationsPlaceholder",
        "standIn.create.noQualifications",
        "standIn.create.submit",
        "standIn.create.success",
        "standIn.create.failed",
        "standIn.create.invalid.count",
        "standIn.create.invalid.pattern",
        "standIn.create.invalid.group",
        "standIn.create.invalid.period",
        "standIn.convert.title",
        "standIn.convert.hint",
        "standIn.convert.targetType",
        "standIn.convert.submit",
        "standIn.convert.success",
        "standIn.convert.failed",
        "standIn.convert.invalid.person",
        "standIn.convert.invalid.address",
        "standIn.persona.displayName",
        "standIn.persona.hint",
        "schedule.toolbar.more-actions",
        "schedule.toolbar.needsGroup",
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
