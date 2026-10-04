// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for the ids the language packs ship in countries.json, states.json and calendar-rules.json. The
/// installers key these rows by id, so an id two packs share made the later pack overwrite the earlier
/// pack's country, state or holiday names (or silently skip its own row); he/id/ms/ro/th once shared one
/// country id. Every id must be unique per table across all packs and must not reuse a GUID literal of the
/// core seed (Infrastructure/Persistence/Seed) or the default geo translation file. Within a pack the natural
/// keys the installers fall back to (country abbreviation, state country prefix + abbreviation, calendar rule
/// country + state + English name) must be unique as well.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class LanguagePluginGeoIdUniquenessGuardTests
{
    private const string IdProperty = "id";
    private const string AbbreviationProperty = "abbreviation";
    private const string CountryPrefixProperty = "countryPrefix";
    private const string CountryProperty = "country";
    private const string StateProperty = "state";
    private const string NameProperty = "name";
    private const string EnglishLanguage = "en";
    private const string KeySeparator = "/";
    private const string DefaultGeoTranslationsFileName = "default-geo-translations.json";
    private const string SeedSourcePattern = "*.cs";
    private const int MinimumPackCount = 20;

    private static readonly string[] GeoFileNames = ["countries.json", "states.json", "calendar-rules.json"];

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly string[] SeedRelativePath = ["Klacks.Api", "Infrastructure", "Persistence", "Seed"];

    private static readonly Regex GuidLiteral = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    [Test]
    public void PackGeoIds_AreUniquePerTableAcrossAllPacks()
    {
        var usages = LoadPackIds();

        var collisions = usages
            .GroupBy(usage => (usage.File, usage.Id))
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.File} {group.Key.Id}: {string.Join(", ", group.Select(usage => usage.Pack))}")
            .ToList();

        TestContext.Progress.WriteLine($"pack geo id guard: {usages.Count} ids checked, {collisions.Count} collisions");
        collisions.ShouldBeEmpty(
            "two language packs ship the same id for different rows of one table; the installers key by id, so the "
            + "later pack overwrites or skips. Give the non-owning pack a new GUID: " + string.Join("; ", collisions));
    }

    [Test]
    public void PackGeoIds_DoNotReuseCoreSeedIds()
    {
        var seedIds = LoadSeedIds();
        seedIds.ShouldNotBeEmpty("no GUID literal found in the core seed; the guard would pass vacantly");

        var collisions = LoadPackIds()
            .Where(usage => seedIds.Contains(usage.Id))
            .Select(usage => $"{usage.Pack}/{usage.File} {usage.Id}")
            .ToList();

        collisions.ShouldBeEmpty(
            "a language pack reuses an id of the core seed: " + string.Join("; ", collisions));
    }

    [Test]
    public void PackGeoNaturalKeys_AreUniqueWithinEachPack()
    {
        var languagesDirectory = LocateDirectory(PluginsLanguagesRelativePath);
        var checkedEntries = 0;
        var duplicates = new List<string>();

        foreach (var pack in Directory.GetDirectories(languagesDirectory))
        {
            foreach (var (fileName, naturalKey) in NaturalKeys)
            {
                var path = Path.Combine(pack, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var keys = document.RootElement.EnumerateArray().Select(naturalKey).ToList();
                checkedEntries += keys.Count;
                duplicates.AddRange(keys
                    .GroupBy(key => key, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => $"{Path.GetFileName(pack)}/{fileName} {group.Key}"));
            }
        }

        checkedEntries.ShouldBeGreaterThan(0);
        duplicates.ShouldBeEmpty(
            "a pack ships two entries with the same natural key; the installers resolve rows by it: "
            + string.Join("; ", duplicates));
    }

    private static readonly (string FileName, Func<JsonElement, string> NaturalKey)[] NaturalKeys =
    [
        ("countries.json", entry => Text(entry, AbbreviationProperty)),
        ("states.json", entry => Text(entry, CountryPrefixProperty) + KeySeparator + Text(entry, AbbreviationProperty)),
        ("calendar-rules.json", entry => Text(entry, CountryProperty) + KeySeparator + Text(entry, StateProperty)
            + KeySeparator + (entry.GetProperty(NameProperty).TryGetProperty(EnglishLanguage, out var english)
                ? english.GetString()
                : string.Empty))
    ];

    private static string Text(JsonElement entry, string property) => entry.GetProperty(property).GetString() ?? string.Empty;

    private static List<(string Pack, string File, Guid Id)> LoadPackIds()
    {
        var languagesDirectory = LocateDirectory(PluginsLanguagesRelativePath);
        var packs = Directory.GetDirectories(languagesDirectory);
        packs.Length.ShouldBeGreaterThanOrEqualTo(MinimumPackCount, $"expected the language packs under {languagesDirectory}");

        var usages = new List<(string Pack, string File, Guid Id)>();
        foreach (var pack in packs)
        {
            var packName = Path.GetFileName(pack);
            foreach (var fileName in GeoFileNames)
            {
                var path = Path.Combine(pack, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    var raw = entry.GetProperty(IdProperty).GetString();
                    Guid.TryParse(raw, out var id).ShouldBeTrue($"{packName}/{fileName} carries an invalid id '{raw}'");
                    usages.Add((packName, fileName, id));
                }
            }
        }

        usages.Select(usage => usage.File).Distinct().Count()
            .ShouldBe(GeoFileNames.Length, "not every geo file type was found; the guard would pass vacantly");
        return usages;
    }

    private static HashSet<Guid> LoadSeedIds()
    {
        var sources = Directory.GetFiles(LocateDirectory(SeedRelativePath), SeedSourcePattern, SearchOption.AllDirectories)
            .Append(Path.Combine(LocateDirectory(PluginsLanguagesRelativePath), DefaultGeoTranslationsFileName));

        return sources
            .SelectMany(path => GuidLiteral.Matches(File.ReadAllText(path)))
            .Select(match => Guid.Parse(match.Value))
            .ToHashSet();
    }

    private static string LocateDirectory(string[] relativePath)
    {
        return RepositoryRootLocator.RequireDirectory(relativePath);
    }
}
