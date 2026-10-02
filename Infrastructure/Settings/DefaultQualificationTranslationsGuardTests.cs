// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard for Plugins/Languages/default-qualification-translations.json, the master file that gives the
/// pre-seeded default qualifications (QualificationsSeed) a name in every language pack. The installer keys
/// rows by the fixed seed id and only fills a missing language, so the file must cover exactly the seeded ids,
/// carry a name for every pack with a manifest (no English fallback, no pack left out), keep numbers and
/// standard codes of the English name (a misaligned id would surface here first), and write non-Latin packs in
/// their own script.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class DefaultQualificationTranslationsGuardTests
{
    private const string MasterFileName = "default-qualification-translations.json";
    private const string ManifestFileName = "manifest.json";
    private const string QualificationsProperty = "qualifications";
    private const string IdProperty = "id";
    private const string NameProperty = "name";
    private const string CodeProperty = "code";
    private const string EnglishLanguage = "en";
    private const string CountryInsertMarker = "INSERT INTO qualification_country";
    private const string VerbatimQuote = "\"\"";
    private const string Quote = "\"";
    private const string EscapedApostrophe = "''";
    private const string Apostrophe = "'";
    private const int MinimumPackCount = 20;
    private const int MinimumSeedCount = 100;
    private const int MaximumNameLength = 200;

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly string[] SeedFileRelativePath =
        ["Klacks.Api", "Infrastructure", "Persistence", "Seed", "QualificationsSeed.cs"];

    private static readonly Regex SeedRow = new(
        "\\('(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})', '(?<name>\\{.*?\\})'::jsonb",
        RegexOptions.Compiled);

    private static readonly Regex EnglishName = new("\"en\":\"(?<en>.*?)\"", RegexOptions.Compiled);

    private static readonly Regex Digits = new("\\d+", RegexOptions.Compiled);

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
    public void MasterFile_CoversExactlyTheSeededQualifications()
    {
        var seeded = LoadSeed();
        seeded.Count.ShouldBeGreaterThanOrEqualTo(MinimumSeedCount, "the seed parse found too few rows; the guard would pass vacantly");

        var master = LoadMaster();
        var missing = seeded.Keys.Except(master.Keys).ToList();
        var unknown = master.Keys.Except(seeded.Keys).ToList();

        missing.ShouldBeEmpty("seeded qualifications without a master entry: " + string.Join(", ", missing));
        unknown.ShouldBeEmpty("master entries that are not seeded qualifications: " + string.Join(", ", unknown));
    }

    [Test]
    public void EveryEntry_CarriesANameForEveryPackAndNothingElse()
    {
        var packCodes = LoadPackCodes();
        packCodes.Count.ShouldBeGreaterThanOrEqualTo(MinimumPackCount);

        var problems = new List<string>();
        foreach (var (id, names) in LoadMaster())
        {
            var missing = packCodes.Except(names.Keys).ToList();
            var extra = names.Keys.Except(packCodes).ToList();
            if (missing.Count > 0)
            {
                problems.Add($"{id} lacks {string.Join("/", missing)}");
            }

            if (extra.Count > 0)
            {
                problems.Add($"{id} has no pack for {string.Join("/", extra)}");
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void EveryName_IsWellFormedAndNotTheEnglishFallback()
    {
        var seeded = LoadSeed();
        var problems = new List<string>();

        foreach (var (id, names) in LoadMaster())
        {
            foreach (var (language, value) in names)
            {
                if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Contains('\n') || value.Contains('\r'))
                {
                    problems.Add($"{id}/{language} is blank, padded or multi-line");
                    continue;
                }

                if (value.Length > MaximumNameLength)
                {
                    problems.Add($"{id}/{language} is longer than {MaximumNameLength} characters");
                }

                if (seeded.TryGetValue(id, out var english) && string.Equals(value, english, StringComparison.Ordinal))
                {
                    problems.Add($"{id}/{language} equals the English name '{english}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void NonLatinPacks_WriteTheirNamesInTheirOwnScript()
    {
        var problems = new List<string>();

        foreach (var (id, names) in LoadMaster())
        {
            foreach (var (language, script) in OwnScript)
            {
                if (names.TryGetValue(language, out var value) && !script.IsMatch(value))
                {
                    problems.Add($"{id}/{language} '{value}' has no character of its own script");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    [Test]
    public void EveryName_KeepsTheNumbersOfTheEnglishName()
    {
        var seeded = LoadSeed();
        var problems = new List<string>();

        foreach (var (id, names) in LoadMaster())
        {
            if (!seeded.TryGetValue(id, out var english))
            {
                continue;
            }

            foreach (var number in Digits.Matches(english).Select(match => match.Value).Distinct())
            {
                foreach (var (language, value) in names)
                {
                    if (!value.Contains(number, StringComparison.Ordinal))
                    {
                        problems.Add($"{id}/{language} '{value}' lost '{number}' of '{english}'");
                    }
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("; ", problems));
    }

    private static Dictionary<string, string> LoadSeed()
    {
        var source = File.ReadAllText(LocateFile(SeedFileRelativePath));
        var qualificationRows = source[..source.IndexOf(CountryInsertMarker, StringComparison.Ordinal)]
            .Replace(VerbatimQuote, Quote)
            .Replace(EscapedApostrophe, Apostrophe);

        return SeedRow.Matches(qualificationRows).ToDictionary(
            match => match.Groups[IdProperty].Value,
            match => EnglishName.Match(match.Groups[NameProperty].Value).Groups[EnglishLanguage].Value);
    }

    private static Dictionary<string, Dictionary<string, string>> LoadMaster()
    {
        var path = Path.Combine(LocateDirectory(PluginsLanguagesRelativePath), MasterFileName);
        File.Exists(path).ShouldBeTrue($"{MasterFileName} is missing under {Path.GetDirectoryName(path)}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var entry in document.RootElement.GetProperty(QualificationsProperty).EnumerateArray())
        {
            var id = entry.GetProperty(IdProperty).GetString()!;
            result.Add(id, entry.GetProperty(NameProperty).EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty));
        }

        return result;
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

    private static string LocateFile(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativePath).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} from {AppContext.BaseDirectory}");
    }

    private static string LocateDirectory(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativePath).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} from {AppContext.BaseDirectory}");
    }
}
