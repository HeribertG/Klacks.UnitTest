// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Freezes the risk class of every skill in skill-seeds.json. SkillRiskClassifier reads
/// InverseSkillRegistry to decide reversibility, and reversibility decides which skills the autonomy
/// gate holds for confirmation - so an edit to that registry can quietly change what Klacksy is allowed
/// to do unattended. The baseline is written once from the UNMODIFIED classifier (WriteBaseline, which
/// is [Explicit] and never runs in CI) and asserted afterwards. A deliberate change is made by editing
/// the baseline in the SAME commit that changes the behaviour, so the diff shows both halves.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SkillRiskReversibilityPinTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string BaselineFileName = "skill-risk-baseline.json";

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory} by walking up from the test base directory.");
    }

    private static string BaselinePath() =>
        Path.Combine(RepositoryRoot().FullName, "Klacks.UnitTest", "Skills", BaselineFileName);

    private static IReadOnlyDictionary<string, SkillRiskClass> ClassifyAllSeeds()
    {
        var seedPath = Path.Combine(
            RepositoryRoot().FullName, ApiProjectDirectory, "Application", "Skills", "Definitions", "skill-seeds.json");
        using var document = JsonDocument.Parse(File.ReadAllText(seedPath));

        var classifier = new SkillRiskClassifier();
        var classified = new Dictionary<string, SkillRiskClass>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = seed.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var category = seed.TryGetProperty("category", out var categoryElement)
                           && Enum.TryParse<SkillCategory>(categoryElement.GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : SkillCategory.Crud;
            var description = seed.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;

            classified[name!] = classifier.Classify(
                new SkillDescriptor(name!, description, category, [], [], [], null));
        }

        return classified;
    }

    [Test]
    [Explicit("Regenerates the baseline. Run deliberately, never as part of a normal test run.")]
    public void WriteBaseline()
    {
        var classified = ClassifyAllSeeds()
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.Ordinal);

        File.WriteAllText(BaselinePath(), JsonSerializer.Serialize(classified, WriteOptions));
        TestContext.WriteLine($"Baseline written: {classified.Count} skills -> {BaselinePath()}");
    }

    [Test]
    public void EverySeedSkill_KeepsItsPinnedRiskClass()
    {
        File.Exists(BaselinePath()).ShouldBeTrue(
            $"{BaselineFileName} is missing - run WriteBaseline once against the unmodified classifier first.");

        var baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(BaselinePath()), ReadOptions) ?? new Dictionary<string, string>();
        var current = ClassifyAllSeeds();
        var drifted = new List<string>();

        foreach (var (name, expected) in baseline)
        {
            if (!current.TryGetValue(name, out var actual))
            {
                drifted.Add($"{name}: pinned as {expected}, no longer in skill-seeds.json");
            }
            else if (!string.Equals(actual.ToString(), expected, StringComparison.Ordinal))
            {
                drifted.Add($"{name}: pinned {expected}, now {actual}");
            }
        }

        drifted.ShouldBeEmpty(
            "The risk class of an existing skill changed, which moves it across the autonomy gate: "
            + string.Join("; ", drifted));
    }

    [Test]
    public void EverySeedSkill_IsCoveredByTheBaseline()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(BaselinePath()), ReadOptions) ?? new Dictionary<string, string>();

        var uncovered = ClassifyAllSeeds().Keys.Where(name => !baseline.ContainsKey(name)).ToList();

        uncovered.ShouldBeEmpty(
            "New skills are not pinned yet; re-run WriteBaseline and review the diff: " + string.Join(", ", uncovered));
    }
}
