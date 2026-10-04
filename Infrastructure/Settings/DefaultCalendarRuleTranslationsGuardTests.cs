// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for Plugins/Languages/default-calendar-rule-translations.json, the master file that gives the pre-seeded
/// holiday rules (CalendarRulesSeed, AdditionalCalendarRulesSeed) a name in every language pack, and for the
/// holiday names shipped in the packs' own calendar-rules.json. Owner rule: every holiday name in all 25
/// languages, no English fallback. The installer keys rows by the fixed seed id and only fills a missing
/// language, so the master file must cover exactly the seeded ids, label each entry with the English name of
/// every id it lists (a misaligned id would surface here first), carry a name for every pack with a manifest,
/// and write non-Latin packs in their own script.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class DefaultCalendarRuleTranslationsGuardTests
{
    private const string MasterFileName = "default-calendar-rule-translations.json";
    private const string PackCalendarRulesFileName = "calendar-rules.json";
    private const string ManifestFileName = "manifest.json";
    private const string CalendarRulesProperty = "calendarRules";
    private const string IdsProperty = "ids";
    private const string IdProperty = "id";
    private const string EnglishLabelProperty = "en";
    private const string NameProperty = "name";
    private const string CodeProperty = "code";
    private const string EnglishLanguage = "en";
    private const string EscapedApostrophe = "''";
    private const string Apostrophe = "'";
    private const int MinimumPackCount = 20;
    private const int MinimumSeedCount = 400;
    private const int MaximumNameLength = 200;

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    /// <summary>
    /// Names that are the customary term in that language although they equal the English one.
    /// </summary>
    private static readonly HashSet<(string English, string Language)> AcceptedLoanwords =
    [
        ("Corpus Christi", "es"),
        ("Mardi Gras", "id"),
        ("Mardi Gras", "ms"),
        ("Memorial Day", "da"),
        ("Memorial Day", "nb"),
        ("Memorial Day", "nl"),
        ("Memorial Day", "sv"),
    ];

    private static readonly Regex SeedRow = new(
        "\\('(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})', '[^']*', '[^']*', (?:true|false), (?:true|false), "
        + "'[^']*', '[^']*', '(?:[^']|'')*', '(?<name>(?:[^']|'')*)'\\)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, Regex> OwnScript = new()
    {
        ["ar"] = new("[\\u0600-\\u06FF]", RegexOptions.Compiled),
        ["he"] = new("[\\u0590-\\u05FF]", RegexOptions.Compiled),
        ["ja"] = new("[\\u3040-\\u30FF\\u4E00-\\u9FFF]", RegexOptions.Compiled),
        ["ko"] = new("[\\uAC00-\\uD7AF]", RegexOptions.Compiled),
        ["th"] = new("[\\u0E00-\\u0E7F]", RegexOptions.Compiled),
        ["el"] = new("[\\u0370-\\u03FF]", RegexOptions.Compiled),
        ["zh-cn"] = new("[\\u4E00-\\u9FFF]", RegexOptions.Compiled),
        ["zh-tw"] = new("[\\u4E00-\\u9FFF]", RegexOptions.Compiled),
    };

    [Test]
    public void MasterFile_CoversExactlyTheSeededHolidayRules()
    {
        var seeded = LoadSeed();
        seeded.Count.ShouldBeGreaterThanOrEqualTo(MinimumSeedCount, "the seed parse found too few rows; the guard would pass vacantly");

        var masterIds = LoadMaster().SelectMany(entry => entry.Ids).ToList();
        var duplicates = masterIds.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
        var missing = seeded.Keys.Except(masterIds).ToList();
        var unknown = masterIds.Except(seeded.Keys).ToList();

        duplicates.ShouldBeEmpty("ids listed under more than one holiday name: " + string.Join(", ", duplicates));
        missing.ShouldBeEmpty("seeded holiday rules without a master entry: " + string.Join(", ", missing));
        unknown.ShouldBeEmpty("master ids that are not seeded holiday rules: " + string.Join(", ", unknown));
    }

    [Test]
    public void EveryEntry_IsLabelledWithTheSeededEnglishNameOfEachOfItsIds()
    {
        var seeded = LoadSeed();
        var problems = LoadMaster()
            .SelectMany(entry => entry.Ids
                .Where(id => seeded.TryGetValue(id, out var english) && english != entry.English)
                .Select(id => $"{id} is seeded as '{seeded[id]}' but listed under '{entry.English}'"))
            .ToList();

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void EveryEntry_CarriesANameForEveryPackAndNothingElse()
    {
        var packCodes = LoadPackCodes();
        packCodes.Count.ShouldBeGreaterThanOrEqualTo(MinimumPackCount);

        var problems = new List<string>();
        foreach (var entry in LoadMaster())
        {
            var missing = packCodes.Except(entry.Names.Keys).ToList();
            var extra = entry.Names.Keys.Except(packCodes).ToList();
            if (missing.Count > 0)
            {
                problems.Add($"'{entry.English}' lacks {string.Join("/", missing)}");
            }

            if (extra.Count > 0)
            {
                problems.Add($"'{entry.English}' has no pack for {string.Join("/", extra)}");
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void EveryName_IsWellFormedAndNotTheEnglishFallback()
    {
        var problems = new List<string>();

        foreach (var entry in LoadMaster())
        {
            foreach (var (language, value) in entry.Names)
            {
                if (!IsWellFormed(value))
                {
                    problems.Add($"'{entry.English}'/{language} is blank, padded, multi-line or too long");
                    continue;
                }

                if (string.Equals(value, entry.English, StringComparison.Ordinal)
                    && !AcceptedLoanwords.Contains((entry.English, language)))
                {
                    problems.Add($"'{entry.English}'/{language} equals the English name");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void NonLatinPacks_WriteTheirNamesInTheirOwnScript()
    {
        var problems = new List<string>();

        foreach (var entry in LoadMaster())
        {
            foreach (var (language, script) in OwnScript)
            {
                if (entry.Names.TryGetValue(language, out var value) && !script.IsMatch(value))
                {
                    problems.Add($"'{entry.English}'/{language} '{value}' has no character of its own script");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void EveryHolidayRuleShippedInAPack_IsNamedInAllLanguages()
    {
        var languages = CoreLanguages.Concat(LoadPackCodes()).ToList();
        var packFiles = Directory.GetDirectories(LocateDirectory(PluginsLanguagesRelativePath))
            .Select(pack => Path.Combine(pack, PackCalendarRulesFileName))
            .Where(File.Exists)
            .ToList();
        packFiles.Count.ShouldBeGreaterThanOrEqualTo(MinimumPackCount);

        var problems = new List<string>();
        foreach (var file in packFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var rule in document.RootElement.EnumerateArray())
            {
                var names = rule.GetProperty(NameProperty).EnumerateObject()
                    .ToDictionary(property => property.Name.ToLowerInvariant(), property => property.Value.GetString() ?? string.Empty);
                var missing = languages.Where(language => !names.TryGetValue(language, out var value) || !IsWellFormed(value)).ToList();
                if (missing.Count > 0)
                {
                    problems.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}/{rule.GetProperty(IdProperty).GetString()} lacks {string.Join("/", missing)}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    private static bool IsWellFormed(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value == value.Trim()
        && !value.Contains('\n')
        && !value.Contains('\r')
        && value.Length <= MaximumNameLength;

    /// <summary>
    /// Runs both seed classes against a MigrationBuilder and reads id and English name of every inserted row from
    /// the generated SQL, so ids generated by AdditionalCalendarRulesSeed.BuildStateRules are covered too.
    /// </summary>
    private static Dictionary<string, string> LoadSeed()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        CalendarRulesSeed.SeedData(builder);
        AdditionalCalendarRulesSeed.SeedData(builder);

        var result = new Dictionary<string, string>();
        foreach (var sql in builder.Operations.OfType<SqlOperation>().Select(operation => operation.Sql))
        {
            foreach (Match match in SeedRow.Matches(sql))
            {
                using var name = JsonDocument.Parse(match.Groups[NameProperty].Value.Replace(EscapedApostrophe, Apostrophe));
                result.Add(match.Groups[IdProperty].Value, name.RootElement.GetProperty(EnglishLanguage).GetString() ?? string.Empty);
            }
        }

        return result;
    }

    private static List<MasterEntry> LoadMaster()
    {
        var path = Path.Combine(LocateDirectory(PluginsLanguagesRelativePath), MasterFileName);
        File.Exists(path).ShouldBeTrue($"{MasterFileName} is missing under {Path.GetDirectoryName(path)}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(CalendarRulesProperty).EnumerateArray()
            .Select(entry => new MasterEntry(
                entry.GetProperty(EnglishLabelProperty).GetString() ?? string.Empty,
                entry.GetProperty(IdsProperty).EnumerateArray().Select(id => id.GetString()!).ToList(),
                entry.GetProperty(NameProperty).EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty)))
            .ToList();
    }

    private static List<string> LoadPackCodes() =>
        Directory.GetDirectories(LocateDirectory(PluginsLanguagesRelativePath))
            .Select(pack => Path.Combine(pack, ManifestFileName))
            .Where(File.Exists)
            .Select(manifest =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                return document.RootElement.GetProperty(CodeProperty).GetString()!.ToLowerInvariant();
            })
            .ToList();

    private static string LocateDirectory(string[] relativePath)
    {
        return RepositoryRootLocator.RequireDirectory(relativePath);
    }

    private sealed record MasterEntry(string English, List<string> Ids, Dictionary<string, string> Names);
}
