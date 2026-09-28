// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every text of the grouping feasibility feature and of the autofill load fix exists in
/// the four core catalogues of Klacks.Ui and in every language plugin, that the inbox sentence shows all
/// four numbers of its event and interpolates nothing else. The Klacks.Ui half reports inconclusive when
/// that repository is not checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class GroupingFeasibilityI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly string[] PlainKeys =
    [
        "setting.proactiveGovernance.kind." + AgentTriggerKinds.GroupingFeasibility,
        "groupingFeasibility.openReport",
        "groupingFeasibility.triggerPhrase",
        "autoWizard.toast.dataIncomplete",
        "autoWizard.toast.scopeChanged",
        "autoWizard.toast.completedForGroup",
        "autoWizard.toast.failedForGroup",
        "autoWizard.runningForGroupTooltip",
        "autoWizard.group.none",
        "wizard.dialog.error.dataIncomplete",
    ];

    private static IReadOnlySet<string> EmittedParameters() =>
        new GroupingFeasibilityTriggerEvent("x", new GroupingFeasibilityCounts(1, 2, 3, 4)).SummaryParams.Keys.ToHashSet(StringComparer.Ordinal);

    [Test]
    public void TheEventEmitsExactlyTheFourNumbers()
    {
        EmittedParameters().ShouldBe(
            new[]
            {
                GroupingFeasibilityTriggerParams.Shifts, GroupingFeasibilityTriggerParams.Clients,
                GroupingFeasibilityTriggerParams.Capacity, GroupingFeasibilityTriggerParams.Proposals
            },
            ignoreOrder: true);
    }

    [Test]
    public void EveryText_ExistsInAllFourCoreCatalogues()
    {
        var directory = FindDirectory(UiCatalogueRelativePath);
        if (directory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        var problems = CoreLanguages.SelectMany(language => ProblemsIn(Path.Combine(directory, language + ".json"))).ToList();
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryText_ExistsInEveryLanguagePlugin()
    {
        var directory = FindDirectory(PluginLanguagesRelativePath);
        directory.ShouldNotBeNull($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");

        var catalogues = Directory.GetDirectories(directory)
            .Select(language => Path.Combine(language, TranslationsFileName))
            .Where(File.Exists)
            .ToList();

        catalogues.Count.ShouldBe(ExpectedPluginCatalogues);
        var problems = catalogues.SelectMany(ProblemsIn).ToList();
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    private static IEnumerable<string> ProblemsIn(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var key in PlainKeys)
        {
            if (!document.RootElement.TryGetProperty(key, out var element) || string.IsNullOrWhiteSpace(element.GetString()))
            {
                yield return $"{path}: '{key}' is missing or empty";
            }
        }

        var summaryKey = ProactiveMessageI18nKeys.GroupingFeasibility;
        if (!document.RootElement.TryGetProperty(summaryKey, out var summary) || string.IsNullOrWhiteSpace(summary.GetString()))
        {
            yield return $"{path}: '{summaryKey}' is missing or empty";
            yield break;
        }

        var used = DoubleBraceTemplate.PlaceholdersOf(summary.GetString()!);
        var emitted = EmittedParameters();
        foreach (var unknown in used.Where(name => !emitted.Contains(name)))
        {
            yield return $"{path}: '{summaryKey}' interpolates {{{{{unknown}}}}}, which its event does not supply";
        }

        foreach (var missing in emitted.Where(name => !used.Contains(name)))
        {
            yield return $"{path}: '{summaryKey}' does not show {{{{{missing}}}}}";
        }
    }

    private static string? FindDirectory(string relativePath)
    {
        var segments = relativePath.Split('/');
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
