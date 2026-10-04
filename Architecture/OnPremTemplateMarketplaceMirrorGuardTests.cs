// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the on-prem bundle: Klacks.Marketplace builds the region downloads offered on the
/// website from its own copy in Resources/OnPremTemplate, while the installer and compose stack are
/// developed in Klacks.Api/deploy/onprem. The copy was synced by hand and fell behind (2026-09-29: the
/// Windows auto-updater fix and the 3584M API limit were missing from every website download), so
/// every template file must be byte-identical to its Klacks.Api counterpart after line-ending
/// normalisation, and every Klacks.Api bundle file must exist in the template.
///
/// Klacks.Marketplace is located as a sibling directory of the Klacks.Api project. Where it is absent
/// the repository was never checked out and the guard reports itself inconclusive, the same split
/// PermissionConstantsUiMirrorGuardTests makes; a Marketplace checkout without the template directory
/// stays a hard failure.
///
/// Scope note — what this guard does NOT cover:
/// - The per-country region profiles (deploy/onprem/regions/*.json). The marketplace adds the patched
///   profile of the requested country to each download itself, so the template carries none.
/// - deploy/onprem/.gitignore, which only matters inside the Klacks.Api repository.
/// - The files the marketplace generates at download time (install-xx wrappers, compose .env.example
///   and README-compose.md in RegionArtifactService).
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class OnPremTemplateMarketplaceMirrorGuardTests
{
    private const string MarketplaceProjectDirectory = "Klacks.Marketplace";
    private const string RegionsDirectory = "regions";
    private const string RegionProfileExtension = ".json";
    private const string GitIgnoreFileName = ".gitignore";
    private const char EntrySeparator = '/';
    private const string AllFilesPattern = "*";
    private const string UnixLineEnding = "\n";

    private static readonly string[] ApiBundleSegments = ["deploy", "onprem"];
    private static readonly string[] MarketplaceTemplateSegments = ["Resources", "OnPremTemplate"];

    [Test]
    public void MarketplaceOnPremTemplate_MustMirrorTheApiOnPremBundle()
    {
        var marketplaceProject = RepositoryRootLocator.FindDirectory(MarketplaceProjectDirectory);
        if (marketplaceProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(MarketplaceProjectDirectory));
            return;
        }

        var apiProject = RepositoryRootLocator.ApiProject;
        var apiBundle = Path.Combine(apiProject, Path.Combine(ApiBundleSegments));
        var template = Path.Combine(marketplaceProject, Path.Combine(MarketplaceTemplateSegments));

        Directory.Exists(template).ShouldBeTrue(
            $"{MarketplaceProjectDirectory} is checked out but has no on-prem template at {template} — " +
            "the website downloads are built from it.");

        var apiFiles = ListFiles(apiBundle).Where(IsMirroredBundleFile).ToList();
        var templateFiles = ListFiles(template);

        var missingInTemplate = apiFiles.Except(templateFiles, StringComparer.Ordinal).ToList();
        var unknownInTemplate = templateFiles.Except(apiFiles, StringComparer.Ordinal).ToList();
        var differing = apiFiles.Intersect(templateFiles, StringComparer.Ordinal)
            .Where(relative => ReadNormalized(apiBundle, relative) != ReadNormalized(template, relative))
            .ToList();

        var problems = new StringBuilder();
        AppendProblems(problems, "missing in the marketplace template", missingInTemplate);
        AppendProblems(problems, "only in the marketplace template", unknownInTemplate);
        AppendProblems(problems, "different from Klacks.Api/deploy/onprem", differing);

        problems.Length.ShouldBe(0,
            "Klacks.Marketplace/Resources/OnPremTemplate has drifted from Klacks.Api/deploy/onprem. " +
            "Copy the Klacks.Api files into the template (LF line endings):" + Environment.NewLine + problems);
    }

    private static bool IsMirroredBundleFile(string relativePath)
    {
        if (relativePath == GitIgnoreFileName)
        {
            return false;
        }

        var isRegionProfile = relativePath.StartsWith(RegionsDirectory + EntrySeparator, StringComparison.Ordinal)
            && relativePath.EndsWith(RegionProfileExtension, StringComparison.Ordinal);
        return !isRegionProfile;
    }

    private static List<string> ListFiles(string root)
    {
        return Directory.EnumerateFiles(root, AllFilesPattern, SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, EntrySeparator))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadNormalized(string root, string relativePath)
    {
        return File.ReadAllText(Path.Combine(root, relativePath)).ReplaceLineEndings(UnixLineEnding);
    }

    private static void AppendProblems(StringBuilder builder, string label, IReadOnlyCollection<string> files)
    {
        foreach (var file in files)
        {
            builder.AppendLine($"  - {file}: {label}");
        }
    }

    private static string LocateApiProject()
    {
        return RepositoryRootLocator.ApiProject;
    }
}
