// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the configurable geo services: the public OpenStreetMap-based hosts (Nominatim, OSRM,
/// OpenRouteService, tile servers) may only be named in Domain/Constants/GeoServiceDefaults.cs. Every caller must
/// resolve its URL through IGeoServicesPolicy, otherwise the NoOutboundTraffic switch and self-hosted instances
/// would silently be bypassed.
///
/// Scans every *.cs file under Klacks.Api (skipping bin/obj/Migrations) and every appsettings*.json (skipping
/// bin/obj). Comment lines are scanned too: a host in a comment is a strong hint of a hard-coded call nearby, and
/// the allowlisted file keeps its hosts out of comments. Not scanned: Plugins/Languages/** and
/// Seed/KlacksyKnowledge/*.md (texts, not code; neither matches the file patterns), deploy/** (nginx CSP and
/// Compose are covered by MapTileCspGuardTests) and other projects (Klacks.Ui has its own Vitest guard).
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ExternalGeoServiceUrlGuardTests
{
    private const string CsFilePattern = "*.cs";
    private const string AppSettingsFilePattern = "appsettings*.json";
    // Same floor as HardCodedTimeZoneGuardTests: a scan of the wrong (near-empty) directory must fail loudly.
    private const int MinimumScannedCsFiles = 4200;
    private const int MinimumScannedJsonFiles = 1;

    private static readonly string[] ForbiddenHosts =
    [
        "nominatim.openstreetmap.org",
        "router.project-osrm.org",
        "api.openrouteservice.org",
        "tile.openstreetmap.org",
        "tile.openstreetmap.de",
        "basemaps.cartocdn.com",
        "opentopomap.org"
    ];

    private static readonly string[] SkippedCsDirectorySegments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"
    ];

    private static readonly string[] SkippedJsonDirectorySegments =
    [
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"
    ];

    private static readonly IReadOnlyDictionary<string, int> AllowedCsLineHits = new Dictionary<string, int>
    {
        ["Domain/Constants/GeoServiceDefaults.cs"] = 7
    };

    [Test]
    public void ApiSourceFiles_MustNotHardCodePublicGeoServiceHosts()
    {
        var (hits, scannedFiles) = Scan(CsFilePattern, SkippedCsDirectorySegments);

        scannedFiles.ShouldBeGreaterThan(
            MinimumScannedCsFiles,
            $"Only {scannedFiles} .cs files were scanned, so the guard cannot have inspected the real Klacks.Api tree.");

        var violations = hits.Where(hit => !AllowedCsLineHits.ContainsKey(hit.Key)).ToList();

        violations.ShouldBeEmpty(
            "Public geo service hosts belong in Domain/Constants/GeoServiceDefaults.cs only. Resolve the URL through " +
            $"IGeoServicesPolicy (Geocoding/Routing/OpenRouteService/MapTiles) instead.{Environment.NewLine}{Report(violations)}");
    }

    [Test]
    public void AllowedCsLineHits_MustStillMatchTheSource()
    {
        var (hits, scannedFiles) = Scan(CsFilePattern, SkippedCsDirectorySegments);

        scannedFiles.ShouldBeGreaterThan(MinimumScannedCsFiles);

        var stale = new StringBuilder();
        foreach (var (path, expected) in AllowedCsLineHits)
        {
            var actual = hits.TryGetValue(path, out var lines) ? lines.Count : 0;
            if (actual != expected)
            {
                stale.AppendLine($"  {path}: expected {expected} line(s) with a public host but found {actual}.");
            }
        }

        stale.Length.ShouldBe(
            0,
            "The allowlist is stale. Review any change in GeoServiceDefaults.cs instead of just adjusting the " +
            $"number.{Environment.NewLine}{stale}");
    }

    [Test]
    public void AppSettingsFiles_MustNotNamePublicGeoServiceHosts()
    {
        var (hits, scannedFiles) = Scan(AppSettingsFilePattern, SkippedJsonDirectorySegments);

        scannedFiles.ShouldBeGreaterThanOrEqualTo(MinimumScannedJsonFiles);

        hits.ShouldBeEmpty(
            "appsettings*.json must not ship a geo service URL: operators configure GeoServices__* via environment, and a " +
            $"shipped URL looks like a switch that does nothing.{Environment.NewLine}{Report(hits.ToList())}");
    }

    private static (Dictionary<string, List<int>> Hits, int ScannedFiles) Scan(string pattern, string[] skippedSegments)
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var hits = new Dictionary<string, List<int>>();
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, pattern, SearchOption.AllDirectories))
        {
            if (skippedSegments.Any(segment => file.Contains(segment, StringComparison.Ordinal)))
            {
                continue;
            }

            scannedFiles++;
            var lines = File.ReadAllLines(file);
            var fileHits = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (ForbiddenHosts.Any(host => lines[i].Contains(host, StringComparison.OrdinalIgnoreCase)))
                {
                    fileHits.Add(i + 1);
                }
            }

            if (fileHits.Count > 0)
            {
                hits[Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/')] = fileHits;
            }
        }

        return (hits, scannedFiles);
    }

    private static string Report(IEnumerable<KeyValuePair<string, List<int>>> hits)
    {
        var report = new StringBuilder();
        foreach (var (path, lines) in hits)
        {
            report.AppendLine($"  {path}: line(s) {string.Join(", ", lines)}");
        }

        return report.ToString();
    }
}
