// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the country region profiles: Klacks.Marketplace seeds its region packages from its own copy in
/// Resources/RegionProfiles and delivers them to new installations (profile JSON, on-prem and compose bundles) and,
/// through the region package auto-update, to existing ones. The source of truth is Klacks.Api/deploy/onprem/regions.
/// The copy was synced by hand and fell behind in all 30 countries (2026-10-23: CH lacked worktime.minRestDays,
/// worktime.maxDailySpanHours, the hospitality Sunday rotation and the reworked qualification catalogs; TH still carried
/// minPauseHours values the Api had removed), so both directories must list the same countries and every Marketplace
/// profile must be semantically equal (JSON tree comparison) to its Klacks.Api counterpart. The top-level "package"
/// block is marketplace metadata and is ignored on both sides.
///
/// Klacks.Marketplace is located as a sibling directory of the Klacks.Api project. Where it is absent the repository
/// was never checked out and the guard reports itself inconclusive, the same split OnPremTemplateMarketplaceMirrorGuardTests
/// makes; a Marketplace checkout without the region profile directory stays a hard failure.
/// </summary>

using System.Text;
using System.Text.Json.Nodes;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class RegionProfileMarketplaceMirrorGuardTests
{
    private const string MarketplaceProjectDirectory = "Klacks.Marketplace";
    private const string RegionProfilePattern = "*.json";
    private const string MarketplacePackagePropertyName = "package";
    private const string RootPath = "$";
    private const int MaxReportedDifferencesPerCountry = 5;

    private static readonly string[] ApiRegionSegments = ["deploy", "onprem", "regions"];
    private static readonly string[] MarketplaceRegionSegments = ["Resources", "RegionProfiles"];

    [Test]
    public void MarketplaceRegionProfiles_MustMirrorTheApiRegionProfiles()
    {
        var marketplaceProject = RepositoryRootLocator.FindDirectory(MarketplaceProjectDirectory);
        if (marketplaceProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(MarketplaceProjectDirectory));
            return;
        }

        var apiRegions = Path.Combine(RepositoryRootLocator.ApiProject, Path.Combine(ApiRegionSegments));
        var marketplaceRegions = Path.Combine(marketplaceProject, Path.Combine(MarketplaceRegionSegments));

        Directory.Exists(marketplaceRegions).ShouldBeTrue(
            $"{MarketplaceProjectDirectory} is checked out but has no region profile directory at {marketplaceRegions} — " +
            "the marketplace seeds its region packages from it.");

        var apiProfiles = ListProfiles(apiRegions);
        var marketplaceProfiles = ListProfiles(marketplaceRegions);

        var problems = new StringBuilder();
        foreach (var country in apiProfiles.Except(marketplaceProfiles, StringComparer.Ordinal))
        {
            problems.AppendLine($"  - {country}: missing in the marketplace");
        }

        foreach (var country in marketplaceProfiles.Except(apiProfiles, StringComparer.Ordinal))
        {
            problems.AppendLine($"  - {country}: only in the marketplace");
        }

        foreach (var country in apiProfiles.Intersect(marketplaceProfiles, StringComparer.Ordinal))
        {
            var apiProfile = ReadProfileWithoutPackage(apiRegions, country);
            var marketplaceProfile = ReadProfileWithoutPackage(marketplaceRegions, country);

            var differences = new List<string>();
            CollectDifferences(apiProfile, marketplaceProfile, RootPath, differences);
            foreach (var path in differences.Take(MaxReportedDifferencesPerCountry))
            {
                problems.AppendLine($"  - {country}: differs at {path}");
            }

            if (differences.Count > MaxReportedDifferencesPerCountry)
            {
                problems.AppendLine($"  - {country}: and {differences.Count - MaxReportedDifferencesPerCountry} more difference(s)");
            }
        }

        problems.Length.ShouldBe(0,
            "Klacks.Marketplace/Resources/RegionProfiles has drifted from Klacks.Api/deploy/onprem/regions. " +
            "Copy the Klacks.Api profiles into the marketplace (LF line endings); only a top-level 'package' block may be added:" +
            Environment.NewLine + problems);
    }

    private static List<string> ListProfiles(string directory)
    {
        return Directory.EnumerateFiles(directory, RegionProfilePattern, SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static JsonNode? ReadProfileWithoutPackage(string directory, string fileName)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, fileName)));
        if (node is JsonObject root)
        {
            root.Remove(MarketplacePackagePropertyName);
        }

        return node;
    }

    private static void CollectDifferences(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            var names = expectedObject.Select(p => p.Key)
                .Union(actualObject.Select(p => p.Key), StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            foreach (var name in names)
            {
                var childPath = $"{path}.{name}";
                if (!expectedObject.ContainsKey(name))
                {
                    differences.Add($"{childPath} (only in the marketplace)");
                }
                else if (!actualObject.ContainsKey(name))
                {
                    differences.Add($"{childPath} (missing in the marketplace)");
                }
                else
                {
                    CollectDifferences(expectedObject[name], actualObject[name], childPath, differences);
                }
            }

            return;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray && expectedArray.Count == actualArray.Count)
        {
            for (var index = 0; index < expectedArray.Count; index++)
            {
                CollectDifferences(expectedArray[index], actualArray[index], $"{path}[{index}]", differences);
            }

            return;
        }

        if (!JsonNode.DeepEquals(expected, actual))
        {
            differences.Add(path);
        }
    }
}
