// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for calendar-rule-legacy-ids.json, the frozen map from a renumbered pack rule to the id it shipped
/// before the 2026-09-30 renumbering. The startup heal only finds a skipped rule through this map, so every entry
/// must point at a rule the pack still ships, its legacy id must differ from every id the pack ships now, and
/// neither the ids nor the legacy ids may repeat - the heal rejects a map with a repeated id and never runs.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginCalendarRuleLegacyIdGuardTests
{
    private const string IdProperty = "id";
    private const string LegacyIdProperty = "legacyId";
    private const int ExpectedLegacyPackCount = 4;

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    [Test]
    public void LegacyIds_PointAtShippedRules_AndDifferFromCurrentIds()
    {
        var languagesDirectory = LocateDirectory(PluginsLanguagesRelativePath);
        var packsWithLegacyIds = 0;
        var problems = new List<string>();

        foreach (var pack in Directory.GetDirectories(languagesDirectory))
        {
            var legacyPath = Path.Combine(pack, LanguagePluginConstants.CalendarRuleLegacyIdsFileName);
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            packsWithLegacyIds++;
            var packName = Path.GetFileName(pack);
            var currentIds = ReadIds(Path.Combine(pack, LanguagePluginConstants.CalendarRulesFileName), IdProperty);

            using var document = JsonDocument.Parse(File.ReadAllText(legacyPath));
            var entries = document.RootElement.EnumerateArray()
                .Select(entry => (
                    Id: Guid.Parse(entry.GetProperty(IdProperty).GetString()!),
                    LegacyId: Guid.Parse(entry.GetProperty(LegacyIdProperty).GetString()!)))
                .ToList();

            var ids = entries.Select(entry => entry.Id).ToList();
            if (ids.Distinct().Count() != ids.Count)
            {
                problems.Add($"{packName}: an id is listed more than once");
            }

            var legacyIds = entries.Select(entry => entry.LegacyId).ToList();
            if (legacyIds.Distinct().Count() != legacyIds.Count)
            {
                problems.Add($"{packName}: a legacy id is listed more than once");
            }

            foreach (var (id, legacyId) in entries)
            {

                if (!currentIds.Contains(id))
                {
                    problems.Add($"{packName}: {id} is not shipped in calendar-rules.json");
                }

                if (currentIds.Contains(legacyId))
                {
                    problems.Add($"{packName}: legacy id {legacyId} is still a current id of the pack");
                }
            }
        }

        packsWithLegacyIds.ShouldBe(ExpectedLegacyPackCount, "expected the legacy id maps of id, ms, pl and ro");
        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    private static HashSet<Guid> ReadIds(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray()
            .Select(entry => Guid.Parse(entry.GetProperty(property).GetString()!))
            .ToHashSet();
    }

    private static string LocateDirectory(string[] relativePath)
    {
        return RepositoryRootLocator.RequireDirectory(relativePath);
    }
}
