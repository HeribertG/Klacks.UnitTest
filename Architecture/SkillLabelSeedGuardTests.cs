// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the authored skill labels in skill-seeds.json and in every feature plugin's own skill-seeds.json.
/// They are the only user-facing skill text that reaches a person without a model call, so every defect here
/// is visible in production and invisible everywhere else: a missing label silently suppresses the
/// clarification for that language, a duplicated one produces "do you mean X or X?", an over-long one is cut
/// mid-word, and a snake_case one leaks an internal identifier. A plugin seed file feeds the very same
/// SkillSeedDefinition into the very same loader, so its labels reach the same clarification and need the
/// same uniqueness - across ALL sources, not per file. Never Assert.Ignore - a skipped guard is a guard that
/// has stopped guarding.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SkillLabelSeedGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SeedRelativePath = "Application/Skills/Definitions/skill-seeds.json";
    private const int ExpectedSkillCount = 480;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // A label without any whitespace that reads like an identifier: CamelCase, or a dotted path such as
    // "Settings.Email". Neither is a noun phrase a person reads; both are how a code identifier gets typed
    // into the label field when the snake_case check alone does not catch it.
    private static readonly Regex CamelCaseShape = new(@"^[A-Za-z]+([A-Z][a-z]+)+$", RegexOptions.Compiled);
    private static readonly Regex DottedPathShape = new(@"[A-Za-z]\.[A-Za-z]", RegexOptions.Compiled);

    private static List<SeededSkill> _mainSeedSkills = null!;
    private static List<SeededSkill> _allSeededSkills = null!;

    private sealed class SeedFile
    {
        public List<SeedSkill> Skills { get; set; } = [];
    }

    private sealed class SeedSkill
    {
        public string Name { get; set; } = string.Empty;

        public Dictionary<string, string>? Labels { get; set; }
    }

    private sealed record SeededSkill(string Source, string Name, Dictionary<string, string>? Labels);

    [OneTimeSetUp]
    public void LoadSeedFilesOnce()
    {
        var apiRoot = LocateApiRoot();

        _mainSeedSkills = Deserialize<SeedFile>(Path.Combine(apiRoot, SeedRelativePath)).Skills
            .Select(skill => new SeededSkill(SeedRelativePath, skill.Name, skill.Labels))
            .ToList();

        _allSeededSkills = [.. _mainSeedSkills];

        var pluginRoot = Path.Combine(apiRoot, FeaturePluginConstants.PluginDirectory);
        if (!Directory.Exists(pluginRoot))
        {
            return;
        }

        foreach (var pluginDirectory in Directory.EnumerateDirectories(pluginRoot).OrderBy(path => path))
        {
            var pluginSeed = Path.Combine(pluginDirectory, FeaturePluginConstants.SkillSeedsFileName);
            if (!File.Exists(pluginSeed))
            {
                continue;
            }

            var source = Path.Combine(
                FeaturePluginConstants.PluginDirectory,
                Path.GetFileName(pluginDirectory),
                FeaturePluginConstants.SkillSeedsFileName);

            _allSeededSkills.AddRange(Deserialize<List<SeedSkill>>(pluginSeed)
                .Select(skill => new SeededSkill(source, skill.Name, skill.Labels)));
        }
    }

    [Test]
    public void TheSeedFile_StillHoldsEverySkillThisGuardWasSizedFor()
    {
        _mainSeedSkills.Count.ShouldBe(
            ExpectedSkillCount,
            $"The main seed file no longer holds {ExpectedSkillCount} skills. Adding or removing a skill is "
            + $"legitimate - raise or lower {nameof(ExpectedSkillCount)} in this file to the new number. What "
            + "this test exists for is the other case: a botched bulk edit of the seed file that drops entries "
            + "shows up here as a red test instead of as skills missing in production.");
    }

    [Test]
    public void EverySkill_HasALabelInEveryCoreLanguage()
    {
        var problems = new List<string>();

        foreach (var skill in _allSeededSkills)
        {
            foreach (var language in LanguagePluginConstants.CoreLanguages)
            {
                if (skill.Labels == null
                    || !skill.Labels.TryGetValue(language, out var label)
                    || string.IsNullOrWhiteSpace(label))
                {
                    problems.Add($"{skill.Source} {skill.Name}: no '{language}' label");
                }
            }
        }

        problems.ShouldBeEmpty(
            $"{problems.Count} missing label(s):{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Test]
    public void NoSkill_CarriesALabelInALanguageTheSeedFileDoesNotOwn()
    {
        var problems = AuthoredLabels()
            .Where(entry => !LanguagePluginConstants.CoreLanguages.Contains(entry.Language))
            .Select(entry => $"{entry.Skill.Source} {entry.Skill.Name}: '{entry.Language}'")
            .ToList();

        problems.ShouldBeEmpty(
            "The seed files own the core languages only; every other language belongs in that pack's "
            + $"skill-labels.json:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Test]
    public void EveryLabel_IsShortEnoughToFitTheQuestion()
    {
        var problems = AuthoredLabels()
            .Where(entry => entry.Label.Trim().Length > GracefulCorrectionDefaults.OptionLabelMaxLength)
            .Select(entry =>
                $"{entry.Skill.Source} {entry.Skill.Name}/{entry.Language}: {entry.Label.Trim().Length} characters")
            .ToList();

        problems.ShouldBeEmpty(
            $"Labels longer than {GracefulCorrectionDefaults.OptionLabelMaxLength} characters are cut "
            + $"mid-word in the question:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    // The label goes into a question frame verbatim, so its own shape has to be clean: stray whitespace
    // survives into the sentence unless every consumer trims it (none of them should have to), and a
    // trailing period puts a full stop in the middle of a question.
    [Test]
    public void NoLabel_CarriesStrayWhitespaceOrATrailingPeriod()
    {
        var problems = AuthoredLabels()
            .Where(entry => entry.Label != entry.Label.Trim() || entry.Label.Trim().EndsWith('.'))
            .Select(entry => $"{entry.Skill.Source} {entry.Skill.Name}/{entry.Language}: '{entry.Label}'")
            .ToList();

        problems.ShouldBeEmpty(
            "A label is written into the question frame verbatim: no leading or trailing whitespace, no "
            + $"sentence-ending period:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    // A label is a noun phrase a person reads, never the internal identifier. InternalIdentifierRedactor
    // exists because snake_case names must not reach a user; this stops one getting there by being typed
    // into the label field in the first place. CamelCase and dotted paths are the two identifier shapes that
    // carry no underscore and would otherwise pass.
    [Test]
    public void NoLabel_IsAnInternalIdentifier()
    {
        var problems = AuthoredLabels()
            .Where(entry => IsInternalIdentifier(entry.Skill.Name, entry.Label))
            .Select(entry => $"{entry.Skill.Source} {entry.Skill.Name}/{entry.Language}: '{entry.Label}'")
            .ToList();

        problems.ShouldBeEmpty(
            "Labels must be user-facing noun phrases, never snake_case, CamelCase or dotted identifiers:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    // Two skills sharing a label make the question unanswerable ("do you mean X or X?"). The composer
    // refuses to ask in that case, which means the defect shows up as a SILENTLY missing question - the
    // reason this has to be caught here and not in production.
    [Test]
    public void NoTwoSkills_ShareALabelWithinOneLanguage()
    {
        var problems = new List<string>();

        foreach (var language in LanguagePluginConstants.CoreLanguages)
        {
            var collisions = _allSeededSkills
                .Where(skill => skill.Labels != null && skill.Labels.ContainsKey(language))
                .GroupBy(skill => skill.Labels![language].Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            problems.AddRange(collisions.Select(group =>
                $"{language}: '{group.Key}' is used by "
                + string.Join(", ", group.Select(skill => $"{skill.Name} ({skill.Source})"))));
        }

        problems.ShouldBeEmpty(
            $"{problems.Count} duplicate label(s):{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    private static T Deserialize<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} deserialized to null.");

    private static string LocateApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory, SeedRelativePath);
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, ApiProjectDirectory);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{SeedRelativePath} by walking up from the test base directory.");
    }

    private static IEnumerable<(SeededSkill Skill, string Language, string Label)> AuthoredLabels() =>
        _allSeededSkills
            .Where(skill => skill.Labels != null)
            .SelectMany(skill => skill.Labels!.Select(entry => (skill, entry.Key, entry.Value)));

    private static bool IsInternalIdentifier(string skillName, string label)
    {
        var trimmed = label.Trim();

        if (trimmed.Contains('_', StringComparison.Ordinal)
            || string.Equals(trimmed, skillName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !trimmed.Any(char.IsWhiteSpace)
               && (CamelCaseShape.IsMatch(trimmed) || DottedPathShape.IsMatch(trimmed));
    }
}
