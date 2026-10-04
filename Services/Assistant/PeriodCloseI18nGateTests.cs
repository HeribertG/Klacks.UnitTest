// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards that every message of the period-close reminders and of the autonomous period close exists in all four
/// core catalogues of Klacks.Ui and in every language plugin, and that no catalogue interpolates a placeholder the
/// emitting event does not supply (it would render raw braces). The messages the follow-up work of 2026-09-26 added
/// (the announcement of the automatic close, the armed-but-braked causes and the per-scan cap) must carry exactly
/// the parameters of their events. Follows EvalRegressionI18nGateTests: the Klacks.Ui half reports inconclusive
/// when that repository is not checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Common;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodCloseI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const int ExpectedPluginCatalogues = 21;

    private static readonly Guid GroupId = Guid.Parse("7a1d0000-0000-0000-0000-00000000000a");
    private static readonly DateOnly PeriodStart = new(2026, 8, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 8, 31);

    private static readonly string[] NewKeys =
    [
        ProactiveMessageI18nKeys.PeriodCloseDueAutoClose,
        ProactiveMessageI18nKeys.PeriodAutoCloseBlockedAutonomyBelowFull,
        ProactiveMessageI18nKeys.PeriodAutoCloseBlockedAdminAutonomyMissing,
        ProactiveMessageI18nKeys.PeriodAutoCloseBlockedTickLimit
    ];

    /// <summary>Every period message key with the parameters its event emits.</summary>
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> EmittedParameters()
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        void Add(IAgentTriggerEvent triggerEvent) =>
            result[triggerEvent.Summary[ProactiveMessageMarkers.I18nPrefix.Length..]] =
                triggerEvent.SummaryParams.Keys.ToHashSet(StringComparer.Ordinal);

        Add(new PeriodCloseDueTriggerEvent(GroupId, "Bern", PeriodEnd, 2));
        Add(new PeriodCloseDueTriggerEvent(GroupId, "Bern", PeriodEnd, 2, 3));
        Add(new PeriodCloseDueTriggerEvent(GroupId, "Bern", PeriodEnd, 2, 3, PeriodEnd.AddDays(3)));
        Add(new PeriodAutoClosedTriggerEvent(GroupId, "Bern", PeriodStart, PeriodEnd, 1, Guid.NewGuid()));
        foreach (var reason in Enum.GetValues<PeriodAutoCloseBlockReason>())
        {
            Add(new PeriodAutoCloseBlockedTriggerEvent(GroupId, "Bern", PeriodStart, PeriodEnd, 0, reason));
        }

        return result;
    }

    [Test]
    public void TheNewMessages_AreEmittedWithExactlyTheirDocumentedParameters()
    {
        var emitted = EmittedParameters();

        emitted[ProactiveMessageI18nKeys.PeriodCloseDueAutoClose]
            .ShouldBe(new[] { "group", "periodEnd", "date", "days" }, ignoreOrder: true);
        emitted[ProactiveMessageI18nKeys.PeriodAutoCloseBlockedTickLimit]
            .ShouldBe(new[] { "group", "from", "until", "errors", "limit" }, ignoreOrder: true);
        emitted[ProactiveMessageI18nKeys.PeriodAutoCloseBlockedAutonomyBelowFull]
            .ShouldBe(new[] { "group", "from", "until", "errors" }, ignoreOrder: true);
    }

    [Test]
    public void EveryPeriodMessage_ExistsInAllFourCoreCatalogues()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
            return;
        }

        var problems = CoreLanguages
            .SelectMany(language => ProblemsIn(Path.Combine(catalogueDirectory, language + ".json")))
            .ToList();

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPeriodMessage_ExistsInEveryLanguagePlugin()
    {
        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
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
        foreach (var (key, parameters) in EmittedParameters())
        {
            if (!document.RootElement.TryGetProperty(key, out var element)
                || string.IsNullOrWhiteSpace(element.GetString()))
            {
                yield return $"{path}: '{key}' is missing or empty";
                continue;
            }

            var used = DoubleBraceTemplate.PlaceholdersOf(element.GetString()!);
            foreach (var unknown in used.Where(name => !parameters.Contains(name)))
            {
                yield return $"{path}: '{key}' interpolates {{{{{unknown}}}}}, which its event does not supply";
            }

            if (NewKeys.Contains(key))
            {
                foreach (var missing in parameters.Where(name => name != "errors" && !used.Contains(name)))
                {
                    yield return $"{path}: '{key}' does not show {{{{{missing}}}}}";
                }
            }
        }
    }

    private static string? FindDirectory(string relativePath)
    {
        return RepositoryRootLocator.FindDirectory(relativePath);
    }
}
