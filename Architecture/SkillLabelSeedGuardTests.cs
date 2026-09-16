// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the authored skill labels in skill-seeds.json. They are the only user-facing skill text that
/// reaches a person without a model call, so every defect here is visible in production and invisible
/// everywhere else: a missing label silently suppresses the clarification for that language, a duplicated
/// one produces "do you mean X or X?", an over-long one is cut mid-word, and a snake_case one leaks an
/// internal identifier. The completeness test is the acceptance criterion of the authoring work and is
/// red until the last batch lands; the well-formedness test is green from the start and goes red the
/// moment a batch introduces a collision. Never Assert.Ignore - a skipped guard is a guard that has
/// stopped guarding.
/// </summary>

using System.Text.Json;
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
    private const int ExpectedSkillCount = 470;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class SeedFile
    {
        public List<SeedSkill> Skills { get; set; } = [];
    }

    private sealed class SeedSkill
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string>? Labels { get; set; }
    }

    private static List<SeedSkill> Skills()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory, SeedRelativePath);
            if (File.Exists(candidate))
            {
                return JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(candidate), JsonOptions)!.Skills;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{SeedRelativePath} by walking up from the test base directory.");
    }

    [Test]
    public void TheSeedFile_StillHoldsEverySkillThisGuardWasSizedFor()
    {
        Skills().Count.ShouldBe(ExpectedSkillCount);
    }

    [Test]
    public void EverySkill_HasALabelInEveryCoreLanguage()
    {
        var problems = new List<string>();

        foreach (var skill in Skills())
        {
            foreach (var language in LanguagePluginConstants.CoreLanguages)
            {
                if (skill.Labels == null
                    || !skill.Labels.TryGetValue(language, out var label)
                    || string.IsNullOrWhiteSpace(label))
                {
                    problems.Add($"{skill.Name}: no '{language}' label");
                }
            }
        }

        problems.ShouldBeEmpty(
            $"{problems.Count} missing label(s):{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Test]
    public void NoSkill_CarriesALabelInALanguageTheSeedFileDoesNotOwn()
    {
        var problems = Skills()
            .Where(skill => skill.Labels != null)
            .SelectMany(skill => skill.Labels!.Keys
                .Where(key => !LanguagePluginConstants.CoreLanguages.Contains(key))
                .Select(key => $"{skill.Name}: '{key}'"))
            .ToList();

        problems.ShouldBeEmpty(
            "The seed file owns the core languages only; every other language belongs in that pack's "
            + $"skill-labels.json:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Test]
    public void EveryLabel_IsShortEnoughToFitTheQuestion()
    {
        var problems = Skills()
            .Where(skill => skill.Labels != null)
            .SelectMany(skill => skill.Labels!
                .Where(entry => entry.Value.Trim().Length > GracefulCorrectionDefaults.OptionLabelMaxLength)
                .Select(entry => $"{skill.Name}/{entry.Key}: {entry.Value.Trim().Length} characters"))
            .ToList();

        problems.ShouldBeEmpty(
            $"Labels longer than {GracefulCorrectionDefaults.OptionLabelMaxLength} characters are cut "
            + $"mid-word in the question:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    // A label is a noun phrase a person reads, never the internal identifier. InternalIdentifierRedactor
    // exists because snake_case names must not reach a user; this stops one getting there by being typed
    // into the label field in the first place.
    [Test]
    public void NoLabel_IsAnInternalIdentifier()
    {
        var problems = Skills()
            .Where(skill => skill.Labels != null)
            .SelectMany(skill => skill.Labels!
                .Where(entry => entry.Value.Contains('_', StringComparison.Ordinal)
                                || string.Equals(entry.Value.Trim(), skill.Name, StringComparison.OrdinalIgnoreCase))
                .Select(entry => $"{skill.Name}/{entry.Key}: '{entry.Value}'"))
            .ToList();

        problems.ShouldBeEmpty(
            $"Labels must be user-facing noun phrases, never snake_case identifiers:"
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
            var collisions = Skills()
                .Where(skill => skill.Labels != null && skill.Labels.ContainsKey(language))
                .GroupBy(skill => skill.Labels![language].Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);

            problems.AddRange(collisions.Select(group =>
                $"{language}: '{group.Key}' is used by {string.Join(", ", group.Select(skill => skill.Name))}"));
        }

        problems.ShouldBeEmpty(
            $"{problems.Count} duplicate label(s):{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }
}
