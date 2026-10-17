// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for Plugins/Languages/default-absence-translations.json, the master file that gives the pre-seeded
/// default absence types (AbsencesSeed plus the on-call and unpaid-training migrations) a name and an
/// abbreviation in every language pack. The installer keys rows by the fixed seed id and only fills a row whose
/// German name still equals the seed name, so the file must cover exactly the seeded ids with their seed German
/// name, carry both fields for every pack with a manifest, and write non-Latin names in their own script.
/// Abbreviations are the input shortcuts of the schedule cells (matched case-insensitively, cut at four
/// characters by the UI), so within one language they must be unique and must not reuse a core-language
/// abbreviation of a different absence type.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class DefaultAbsenceTranslationsGuardTests
{
    private const string MasterFileName = "default-absence-translations.json";
    private const string ManifestFileName = "manifest.json";
    private const string AbsencesProperty = "absences";
    private const string IdProperty = "id";
    private const string SeedNameProperty = "de";
    private const string NameProperty = "name";
    private const string AbbreviationProperty = "abbreviation";
    private const string CodeProperty = "code";
    private const string GermanLanguage = "de";
    private const string EnglishLanguage = "en";
    private const string VerbatimQuote = "\"\"";
    private const string Quote = "\"";
    private const string HalfIllnessId = "1d5a1964-7fad-4da9-945c-3ad00c0edaa8";
    private const string HalfIllnessMarker = "50";
    private const int ExpectedSeedCount = 11;
    private const int MinimumPackCount = 20;
    private const int MaximumNameLength = 100;
    private const int MaximumAbbreviationLength = 4;

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly string[][] SeedSourceRelativePaths =
    [
        ["Klacks.Api", "Infrastructure", "Persistence", "Seed", "AbsencesSeed.cs"],
        ["Klacks.Api", "Infrastructure", "Persistence", "Migrations", "20260727085746_AddPikettOnCallAbsence.cs"],
        ["Klacks.Api", "Infrastructure", "Persistence", "Migrations", "20260819071844_SplitTrainingAndWirePaidAbsence.cs"],
    ];

    private static readonly Regex SeedRow = new(
        "'(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})','#[0-9a-fA-F]{6}',[^']*'\\{.*?\\}',(?:true|false),'(?<name>\\{.*?\\})','(?<abbreviation>\\{.*?\\})'",
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
    public void Seed_ParsesTheElevenDefaultAbsenceTypes()
    {
        LoadSeed().Count.ShouldBe(ExpectedSeedCount, "the seed parse must find exactly the eleven default absence types; the guard would otherwise pass vacantly");
    }

    [Test]
    public void MasterFile_CoversExactlyTheSeededAbsenceTypesWithTheirSeedGermanName()
    {
        var seeded = LoadSeed();
        var master = LoadMaster();

        var missing = seeded.Keys.Except(master.Keys).ToList();
        var unknown = master.Keys.Except(seeded.Keys).ToList();
        missing.ShouldBeEmpty("seeded absence types without a master entry: " + string.Join(", ", missing));
        unknown.ShouldBeEmpty("master entries that are not seeded absence types: " + string.Join(", ", unknown));

        var wrongSeedName = master
            .Where(entry => seeded.TryGetValue(entry.Key, out var seed) && seed.Name[GermanLanguage] != entry.Value.SeedName)
            .Select(entry => $"{entry.Key} '{entry.Value.SeedName}' != seed '{seeded[entry.Key].Name[GermanLanguage]}'")
            .ToList();
        wrongSeedName.ShouldBeEmpty("the installer guards on the seed German name: " + string.Join("; ", wrongSeedName));
    }

    [Test]
    public void EveryEntry_CarriesNameAndAbbreviationForEveryPackAndNothingElse()
    {
        var packCodes = LoadPackCodes();
        packCodes.Count.ShouldBeGreaterThanOrEqualTo(MinimumPackCount);

        var problems = new List<string>();
        foreach (var (id, entry) in LoadMaster())
        {
            foreach (var (field, values) in new[] { (NameProperty, entry.Names), (AbbreviationProperty, entry.Abbreviations) })
            {
                var missing = packCodes.Except(values.Keys).ToList();
                var extra = values.Keys.Except(packCodes).ToList();
                if (missing.Count > 0)
                {
                    problems.Add($"{id}/{field} lacks {string.Join("/", missing)}");
                }

                if (extra.Count > 0)
                {
                    problems.Add($"{id}/{field} has no pack for {string.Join("/", extra)}");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void EveryValue_IsWellFormedAndNoNameIsTheEnglishFallback()
    {
        var seeded = LoadSeed();
        var problems = new List<string>();

        foreach (var (id, entry) in LoadMaster())
        {
            foreach (var (language, value) in entry.Names)
            {
                if (!IsWellFormed(value) || value.Length > MaximumNameLength)
                {
                    problems.Add($"{id}/name/{language} is blank, padded, multi-line or too long");
                }

                if (seeded.TryGetValue(id, out var seed) && string.Equals(value, seed.Name[EnglishLanguage], StringComparison.Ordinal))
                {
                    problems.Add($"{id}/name/{language} equals the English name '{value}'");
                }
            }

            foreach (var (language, value) in entry.Abbreviations)
            {
                if (!IsWellFormed(value) || value.Contains(' '))
                {
                    problems.Add($"{id}/abbreviation/{language} '{value}' is blank, padded or contains a space");
                }

                if (value.Length > MaximumAbbreviationLength)
                {
                    problems.Add($"{id}/abbreviation/{language} '{value}' is longer than {MaximumAbbreviationLength} UTF-16 units; the UI would cut it");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void NonLatinPacks_WriteTheirNamesInTheirOwnScript()
    {
        var problems = new List<string>();

        foreach (var (id, entry) in LoadMaster())
        {
            foreach (var (language, script) in OwnScript)
            {
                if (entry.Names.TryGetValue(language, out var value) && !script.IsMatch(value))
                {
                    problems.Add($"{id}/{language} '{value}' has no character of its own script");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void HalfIllnessNames_KeepTheFiftyPercent()
    {
        var entry = LoadMaster()[HalfIllnessId];
        var problems = entry.Names
            .Where(pair => !pair.Value.Contains(HalfIllnessMarker, StringComparison.Ordinal))
            .Select(pair => $"{pair.Key} '{pair.Value}'")
            .ToList();

        problems.ShouldBeEmpty("the 50% type must stay distinguishable from the full one: " + string.Join("; ", problems));
    }

    [Test]
    public void Abbreviations_AreUniqueWithinEachLanguage()
    {
        var problems = new List<string>();
        var master = LoadMaster();

        foreach (var language in LoadPackCodes())
        {
            var duplicates = master
                .Where(entry => entry.Value.Abbreviations.ContainsKey(language))
                .GroupBy(entry => entry.Value.Abbreviations[language].ToUpperInvariant())
                .Where(group => group.Count() > 1)
                .Select(group => $"{language} '{group.Key}' used by {string.Join("/", group.Select(entry => entry.Key))}");
            problems.AddRange(duplicates);
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void Abbreviations_DoNotReuseACoreAbbreviationOfAnotherAbsenceType()
    {
        var seeded = LoadSeed();
        var problems = new List<string>();

        foreach (var (id, entry) in LoadMaster())
        {
            foreach (var (language, value) in entry.Abbreviations)
            {
                var upper = value.ToUpperInvariant();
                foreach (var (otherId, other) in seeded.Where(seed => seed.Key != id))
                {
                    var clash = other.Abbreviations.FirstOrDefault(core => core.Value.ToUpperInvariant() == upper);
                    if (clash.Key != null)
                    {
                        problems.Add($"{id}/{language} '{value}' equals the {clash.Key} abbreviation of {otherId}");
                    }
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    private static bool IsWellFormed(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Contains('\n') && !value.Contains('\r');

    private static Dictionary<string, SeedAbsence> LoadSeed()
    {
        var result = new Dictionary<string, SeedAbsence>();
        foreach (var relativePath in SeedSourceRelativePaths)
        {
            var source = File.ReadAllText(RepositoryRootLocator.RequireFile(relativePath));
            foreach (Match match in SeedRow.Matches(source))
            {
                result[match.Groups[IdProperty].Value] = new SeedAbsence(
                    ParseJsonObject(match.Groups[NameProperty].Value),
                    ParseJsonObject(match.Groups[AbbreviationProperty].Value));
            }
        }

        return result;
    }

    private static Dictionary<string, string> ParseJsonObject(string verbatimJson)
    {
        using var document = JsonDocument.Parse(verbatimJson.Replace(VerbatimQuote, Quote));
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
    }

    private static Dictionary<string, MasterAbsence> LoadMaster()
    {
        var path = Path.Combine(RepositoryRootLocator.RequireDirectory(PluginsLanguagesRelativePath), MasterFileName);
        File.Exists(path).ShouldBeTrue($"{MasterFileName} is missing under {Path.GetDirectoryName(path)}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, MasterAbsence>();
        foreach (var entry in document.RootElement.GetProperty(AbsencesProperty).EnumerateArray())
        {
            result.Add(entry.GetProperty(IdProperty).GetString()!, new MasterAbsence(
                entry.GetProperty(SeedNameProperty).GetString()!,
                ReadLanguageMap(entry.GetProperty(NameProperty)),
                ReadLanguageMap(entry.GetProperty(AbbreviationProperty))));
        }

        return result;
    }

    private static Dictionary<string, string> ReadLanguageMap(JsonElement element) =>
        element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);

    private static List<string> LoadPackCodes() =>
        Directory.GetDirectories(RepositoryRootLocator.RequireDirectory(PluginsLanguagesRelativePath))
            .Select(pack => Path.Combine(pack, ManifestFileName))
            .Where(File.Exists)
            .Select(manifest =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                return document.RootElement.GetProperty(CodeProperty).GetString()!.ToLowerInvariant();
            })
            .ToList();

    private sealed record SeedAbsence(Dictionary<string, string> Name, Dictionary<string, string> Abbreviations);

    private sealed record MasterAbsence(string SeedName, Dictionary<string, string> Names, Dictionary<string, string> Abbreviations);
}
