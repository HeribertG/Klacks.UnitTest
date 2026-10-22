// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the hand-written route tables of the assistant: every route string they carry must be a page
/// the Angular router really knows, read from the Klacksy page-key catalog the Ui scanner generates into the Api
/// (Application/Skills/Definitions/klacksy-page-keys.generated.json). Incident 2026-10-22: the welcome suggestion
/// "Manage LLM providers" navigated to "/workplace/settings/llm", a route that never existed - the router threw
/// NG04002 and the click did nothing. The ranker and the retrieval boost tables carried the same dead route (and
/// "/workplace/clients"), so their route signal silently never fired.
///
/// A welcome suggestion that scrolls to a section must name a navigation target that exists on its route and is not
/// obsolete (navigation-targets.json), otherwise the scroll is a target miss.
///
/// Scope note - what this guard does NOT cover: routes assembled at runtime, route literals outside these four
/// tables, and whether the page-key catalog itself is in sync with the Angular router (the Ui side checks that).
/// </summary>

using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AssistantRouteTablesPageKeyGuardTests
{
    private const int MinimumCatalogRoutes = 10;
    private const string PageKeyManifestFileName = "klacksy-page-keys.generated.json";
    private const string TargetManifestFileName = "navigation-targets.json";
    private const string TargetIdProperty = "targetId";
    private const string RouteProperty = "route";
    private const string ObsoleteProperty = "obsolete";
    private const char QuerySeparator = '?';

    private static readonly string[] ManifestPathSegments = ["Application", "Skills", "Definitions"];

    [Test]
    public void EveryAssistantRouteTableEntry_MustBeARouteOfThePageKeyCatalog()
    {
        var knownRoutes = LoadCatalogRoutes();
        knownRoutes.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumCatalogRoutes,
            $"Only {knownRoutes.Count} routes were read from {PageKeyManifestFileName}; the guard cannot have " +
            "inspected the real catalog, so a green result would be meaningless.");

        var used = new List<(string Table, string Route)>();
        used.AddRange(GetWelcomeQueryHandler.SuggestionKeyRoutes.Select(e => ("GetWelcomeQueryHandler.SuggestionKeyRoutes", e.Value)));
        used.AddRange(SuggestionsRanker.RouteHints.Select(e => ("SuggestionsRanker.RouteHints", e.RouteFragment)));
        used.AddRange(KnowledgeRetrievalService.RouteSkillBoosts.Select(e => ("KnowledgeRetrievalService.RouteSkillBoosts", e.RouteFragment)));
        used.AddRange(PageExplainSkillRoutes.RouteToSkill.Keys.Select(route => ("PageExplainSkillRoutes.RouteToSkill", route)));

        var violations = used
            .Where(entry => !knownRoutes.Contains(entry.Route))
            .Select(entry => $"{entry.Table}: '{entry.Route}'")
            .ToList();

        violations.ShouldBeEmpty(Report(
            "These assistant route-table entries name no route of the Klacksy page-key catalog. A navigation to "
            + "them ends in the Angular router's NG04002, and a route-keyed lookup on them never matches:",
            violations));
    }

    [Test]
    public void EveryWelcomeSuggestionTarget_MustBeALiveNavigationTargetOnItsRoute()
    {
        var targets = LoadTargets();
        var violations = new List<string>();

        foreach (var (key, target) in GetWelcomeQueryHandler.SuggestionKeyTargets)
        {
            if (!GetWelcomeQueryHandler.SuggestionKeyRoutes.TryGetValue(key, out var route))
            {
                violations.Add($"{key}: target '{target}' without a route");
                continue;
            }

            var matches = targets.Any(t => t.TargetId == target && t.Route == route && !t.Obsolete);
            if (!matches)
            {
                violations.Add($"{key}: target '{target}' is no live navigation target on '{route}'");
            }
        }

        violations.ShouldBeEmpty(Report(
            $"These welcome suggestion targets do not exist in {TargetManifestFileName} on the suggestion's route:",
            violations));
    }

    private static HashSet<string> LoadCatalogRoutes()
    {
        var catalog = new KlacksyPageKeyCatalog(ManifestPath(PageKeyManifestFileName));
        return catalog.All
            .Select(entry => entry.Route.Split(QuerySeparator)[0])
            .ToHashSet(StringComparer.Ordinal);
    }

    private static List<(string TargetId, string Route, bool Obsolete)> LoadTargets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath(TargetManifestFileName)));
        return document.RootElement
            .EnumerateArray()
            .Select(element => (
                element.GetProperty(TargetIdProperty).GetString() ?? string.Empty,
                element.TryGetProperty(RouteProperty, out var route) ? route.GetString() ?? string.Empty : string.Empty,
                element.TryGetProperty(ObsoleteProperty, out var obsolete) && obsolete.GetBoolean()))
            .ToList();
    }

    private static string ManifestPath(string fileName)
        => Path.Combine(RepositoryRootLocator.ApiProject, Path.Combine(ManifestPathSegments), fileName);

    private static string Report(string headline, IEnumerable<string> violations)
    {
        var report = new StringBuilder(headline).AppendLine();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation}");
        }

        return report.ToString();
    }
}
