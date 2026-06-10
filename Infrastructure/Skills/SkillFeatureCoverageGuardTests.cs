// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Feature-drift detector: enumerates every API controller via reflection and checks it against
/// the curated coverage map in SkillFeatureCoverageMap. A new controller without an explicit
/// Klacksy coverage decision (covered with skills, or excluded with a reason) turns the build red,
/// so every new feature forces an answer to "can Klacksy do this via chat?". A second guard verifies
/// that every skill referenced by a covered entry actually exists in the skill seed definitions.
/// </summary>

using System.Text.Json;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillFeatureCoverageGuardTests
{
    private const string ControllerNamespacePrefix = "Klacks.Api.Presentation.Controllers";
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SettingsReaderSkillsFileName = "settings-reader-skills.json";
    private const string SkillsJsonProperty = "skills";
    private const string SkillNameJsonProperty = "name";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    public static IEnumerable<Type> AllControllerTypes()
    {
        return typeof(BaseController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => t.Namespace != null && t.Namespace.StartsWith(ControllerNamespacePrefix, StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    [TestCaseSource(nameof(AllControllerTypes))]
    public void Controller_MustHaveKlacksyCoverageDecision(Type controllerType)
    {
        SkillFeatureCoverageMap.Decisions.ContainsKey(controllerType.Name).ShouldBeTrue(
            $"New controller {controllerType.Name} has no Klacksy coverage decision — add skills or an explicit exclusion. " +
            $"Register it in SkillFeatureCoverageMap.Decisions as either '{SkillFeatureCoverageMap.CoveredPrefix}<skill names>' " +
            $"or '{SkillFeatureCoverageMap.ExcludedPrefix}<reason>'.");

        var justification = SkillFeatureCoverageMap.Decisions[controllerType.Name];

        (justification.StartsWith(SkillFeatureCoverageMap.CoveredPrefix, StringComparison.Ordinal) ||
         justification.StartsWith(SkillFeatureCoverageMap.ExcludedPrefix, StringComparison.Ordinal)).ShouldBeTrue(
            $"Coverage decision for {controllerType.Name} must start with '{SkillFeatureCoverageMap.CoveredPrefix}' " +
            $"or '{SkillFeatureCoverageMap.ExcludedPrefix}' but was: '{justification}'.");
    }

    [Test]
    public void CoverageMap_MustNotContainStaleControllerEntries()
    {
        var controllerNames = AllControllerTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var staleEntries = SkillFeatureCoverageMap.Decisions.Keys
            .Where(name => !controllerNames.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        staleEntries.ShouldBeEmpty(
            "SkillFeatureCoverageMap contains entries without a matching controller (renamed or deleted?): " +
            $"{string.Join(", ", staleEntries)}. Update or remove these entries so the map stays an honest inventory.");
    }

    [Test]
    public void CoveredEntries_MustReferenceExistingSkills()
    {
        var knownSkillNames = LoadKnownSkillNames();
        var violations = new List<string>();

        foreach (var (controllerName, justification) in SkillFeatureCoverageMap.Decisions)
        {
            if (!justification.StartsWith(SkillFeatureCoverageMap.CoveredPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var referencedSkills = justification[SkillFeatureCoverageMap.CoveredPrefix.Length..]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (referencedSkills.Length == 0)
            {
                violations.Add($"{controllerName}: covered entry references no skill at all");
                continue;
            }

            foreach (var skillName in referencedSkills)
            {
                if (!knownSkillNames.Contains(skillName))
                {
                    violations.Add($"{controllerName}: skill '{skillName}' does not exist in {SkillSeedsFileName} or {SettingsReaderSkillsFileName}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "Covered coverage-map entries must reference real skills from the seed definitions. Violations: " +
            string.Join("; ", violations));
    }

    private static HashSet<string> LoadKnownSkillNames()
    {
        var skillNames = new HashSet<string>(StringComparer.Ordinal);
        CollectSkillNames(LocateDefinitionsFile(SkillSeedsFileName), skillNames);
        CollectSkillNames(LocateDefinitionsFile(SettingsReaderSkillsFileName), skillNames);
        return skillNames;
    }

    private static void CollectSkillNames(string filePath, HashSet<string> skillNames)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        foreach (var skill in document.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
        {
            if (skill.TryGetProperty(SkillNameJsonProperty, out var name) && name.ValueKind == JsonValueKind.String)
            {
                skillNames.Add(name.GetString()!);
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
}
