// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard: every JSON file a language pack ships (Klacks.Api/Plugins/Languages/code) reaches a pack
/// installed from the Klacks Store. The store bundle has own keys for manifest, translations, docs, countries,
/// states and calendar rules; every other file must be in LanguagePluginConstants.StoreBundleFiles, because the
/// store install writes only those names. Without this guard a new pack file would silently be missing from
/// store installs (as assistant-texts.json was until 2026-10-23).
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class LanguagePackStoreBundleGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagesRelativePath = "Plugins/Languages";
    private const string JsonSearchPattern = "*.json";
    private const string DocsIndexFileName = "docs.json";

    private static readonly IReadOnlySet<string> BundleKeyFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        LanguagePluginConstants.ManifestFileName,
        LanguagePluginConstants.TranslationsFileName,
        LanguagePluginConstants.CountriesFileName,
        LanguagePluginConstants.StatesFileName,
        LanguagePluginConstants.CalendarRulesFileName,
        DocsIndexFileName,
    };

    [Test]
    public void EveryPackFile_ReachesStoreInstalls()
    {
        var languagesRoot = new DirectoryInfo(RepositoryRootLocator.RequireDirectory(ApiProjectDirectory, LanguagesRelativePath));
        var packs = languagesRoot.GetDirectories();
        packs.ShouldNotBeEmpty($"No language packs found under {LanguagesRelativePath}; the guard would pass vacantly.");

        var uncovered = packs
            .SelectMany(pack => pack.GetFiles(JsonSearchPattern).Select(file => $"{pack.Name}/{file.Name}"))
            .Where(path => !IsCovered(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        uncovered.ShouldBeEmpty(
            "Pack files missing from LanguagePluginConstants.StoreBundleFiles (store installs would lack them): "
            + string.Join(", ", uncovered));
    }

    [Test]
    public void BundleFilesAndBundleKeys_DoNotOverlap()
    {
        LanguagePluginConstants.StoreBundleFiles.Intersect(BundleKeyFiles).ShouldBeEmpty();
    }

    private static bool IsCovered(string fileName) =>
        BundleKeyFiles.Contains(fileName) || LanguagePluginConstants.StoreBundleFiles.Contains(fileName);
}
