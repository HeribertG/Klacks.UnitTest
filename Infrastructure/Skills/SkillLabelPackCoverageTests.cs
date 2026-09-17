// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Every language pack that ships a skill-labels.json must cover every seeded skill with a well-formed
/// label. Deliberately NOT "every pack ships the file": the 21 real files are generated later with the
/// owner's paid generator key and are out of scope here, so a test demanding them would be red from the
/// day it was written - and a red guard nobody can fix gets disabled, which is worse than no guard. What
/// is guarded is what can be true today: a file that exists is complete, and the moment the generator
/// lands its output is checked in full. Whoever ships those 21 files tightens this to demand them, in
/// the commit that ships them. No Assert.Ignore anywhere - an empty set of packs passes because nothing
/// is broken, not because the check was skipped.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillLabelPackCoverageTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SeedRelativePath = "Application/Skills/Definitions/skill-seeds.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class SeedFile
    {
        public List<SeedSkill> Skills { get; set; } = [];
    }

    private sealed class SeedSkill
    {
        public string Name { get; set; } = string.Empty;
    }

    private static DirectoryInfo ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, SeedRelativePath)))
            {
                return new DirectoryInfo(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory} by walking up from the test base directory.");
    }

    private static HashSet<string> SeededSkillNames()
    {
        var json = File.ReadAllText(Path.Combine(ApiRoot().FullName, SeedRelativePath));

        return JsonSerializer.Deserialize<SeedFile>(json, JsonOptions)!.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> PackLabelFiles() =>
        Directory.GetDirectories(Path.Combine(ApiRoot().FullName, "Plugins", "Languages"))
            .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
            .Select(dir => Path.Combine(dir, LanguagePluginConstants.SkillLabelsFileName))
            .Where(File.Exists);

    [Test]
    public void EveryPresentLabelFile_CoversEverySeededSkill()
    {
        var seeded = SeededSkillNames();
        var problems = new List<string>();

        foreach (var file in PackLabelFiles())
        {
            var code = Path.GetFileName(Path.GetDirectoryName(file))!;
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                             File.ReadAllText(file), JsonOptions)
                         ?? new Dictionary<string, string>();

            var missing = seeded
                .Where(name => !labels.TryGetValue(name, out var label) || string.IsNullOrWhiteSpace(label))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (missing.Count > 0)
            {
                problems.Add($"{code}: {missing.Count} skill(s) without a label, first: {string.Join(", ", missing.Take(5))}");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void NoPresentLabelFile_NamesASkillThatDoesNotExist()
    {
        var seeded = SeededSkillNames();
        var problems = new List<string>();

        foreach (var file in PackLabelFiles())
        {
            var code = Path.GetFileName(Path.GetDirectoryName(file))!;
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                             File.ReadAllText(file), JsonOptions)
                         ?? new Dictionary<string, string>();

            var orphans = labels.Keys.Where(name => !seeded.Contains(name)).ToList();
            if (orphans.Count > 0)
            {
                problems.Add($"{code}: unknown skill(s) {string.Join(", ", orphans.Take(5))}");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void EveryPresentLabel_IsWellFormed()
    {
        var problems = new List<string>();

        foreach (var file in PackLabelFiles())
        {
            var code = Path.GetFileName(Path.GetDirectoryName(file))!;
            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                             File.ReadAllText(file), JsonOptions)
                         ?? new Dictionary<string, string>();

            problems.AddRange(labels
                .Where(entry => entry.Value.Trim().Length > GracefulCorrectionDefaults.OptionLabelMaxLength
                                || entry.Value.Contains('_', StringComparison.Ordinal))
                .Select(entry => $"{code}/{entry.Key}: '{entry.Value}'"));

            problems.AddRange(labels
                .GroupBy(entry => entry.Value.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => $"{code}: '{group.Key}' used by {string.Join(", ", group.Select(e => e.Key))}"));
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }
}
