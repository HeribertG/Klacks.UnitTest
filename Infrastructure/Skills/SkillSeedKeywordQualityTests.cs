// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W2.5 gate for trigger keywords in skill-seeds.json (core and feature-plugin): a keyword must be
/// long enough that SkillMatchingEngine can actually match it, otherwise it is dead weight that
/// looks like a working trigger.
///
/// The rule MIRRORS the engine instead of imposing a flat minimum, because the engine has two
/// thresholds: a phrase of four characters or more is matched as a substring, while a SHORTER phrase
/// is matched only when it sits in the language-neutral "mul" group, and then only on a word
/// boundary (SkillMatchingEngine.MinAnchorMatchLength). That anchor rule is deliberate and measured
/// (2026-07-31): sso, stt, tts, mcp, pat, bmd and 24h are the terms that survive translation into
/// non-Latin scripts, and a flat four-character minimum would delete exactly them. Below four
/// characters, and outside "mul", nothing can ever match — those are the entries this gate forbids.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedKeywordQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    /// <summary>Mirrors SkillMatchingEngine.MinMatchLength (substring matching).</summary>
    private const int SubstringMinLength = 4;

    /// <summary>Mirrors SkillMatchingEngine.MinAnchorMatchLength (whole-word matching in "mul").</summary>
    private const int AnchorMinLength = 2;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    [Test]
    public void EveryTriggerKeyword_IsLongEnoughToEverMatch()
    {
        var violations = EnumerateKeywords()
            .Where(entry => entry.Keyword.Trim().Length < MinimumLengthFor(entry.Language))
            .Select(entry => $"{entry.SkillName}/{entry.Language}: '{entry.Keyword}'")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            $"A trigger keyword shorter than {SubstringMinLength} characters is never matched by " +
            "SkillMatchingEngine unless it sits in the language-neutral " +
            $"'{SkillPhraseLanguages.Multiple}' group, where the whole-word anchor rule accepts " +
            $"{AnchorMinLength} characters. Either move the term into that group or drop it. " +
            "Offenders: " + string.Join(" | ", violations));
    }

    [Test]
    public void NoTriggerKeyword_IsBlankOrPadded()
    {
        var violations = EnumerateKeywords()
            .Where(entry => entry.Keyword.Length != entry.Keyword.Trim().Length
                || entry.Keyword.Trim().Length == 0)
            .Select(entry => $"{entry.SkillName}/{entry.Language}: '{entry.Keyword}'")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "Trigger keywords are lower-cased and compared verbatim, so leading or trailing " +
            "whitespace silently changes what matches. Offenders: " + string.Join(" | ", violations));
    }

    private static int MinimumLengthFor(string language) =>
        string.Equals(language, SkillPhraseLanguages.Multiple, StringComparison.OrdinalIgnoreCase)
            ? AnchorMinLength
            : SubstringMinLength;

    private static IEnumerable<(string SkillName, string Language, string Keyword)> EnumerateKeywords()
    {
        foreach (var file in LocateSeedFiles())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var skills = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.GetProperty("skills");

            foreach (var skill in skills.EnumerateArray())
            {
                var name = skill.GetProperty("name").GetString() ?? string.Empty;
                if (!skill.TryGetProperty("triggerKeywords", out var keywords))
                {
                    continue;
                }

                foreach (var entry in ReadGroups(name, keywords))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>
    /// Reads both stored shapes, exactly as TriggerKeywordGroupsConverter does: an object keyed by
    /// language, or a legacy flat array that counts as the Undetermined group.
    /// </summary>
    private static IEnumerable<(string SkillName, string Language, string Keyword)> ReadGroups(
        string skillName, JsonElement keywords)
    {
        if (keywords.ValueKind == JsonValueKind.Array)
        {
            foreach (var keyword in keywords.EnumerateArray())
            {
                yield return (skillName, SkillPhraseLanguages.Undetermined, keyword.GetString() ?? string.Empty);
            }

            yield break;
        }

        if (keywords.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var group in keywords.EnumerateObject())
        {
            foreach (var keyword in group.Value.EnumerateArray())
            {
                yield return (skillName, group.Name, keyword.GetString() ?? string.Empty);
            }
        }
    }

    private static IEnumerable<string> LocateSeedFiles()
    {
        yield return LocateDefinitionsFile(SkillSeedsFileName);

        var pluginsDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (pluginsDir == null)
        {
            yield break;
        }

        foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
        {
            var pluginSeedFile = Path.Combine(pluginDir, SkillSeedsFileName);
            if (File.Exists(pluginSeedFile))
            {
                yield return pluginSeedFile;
            }
        }
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");
    }

    private static string? TryLocateDir(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
