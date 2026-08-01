// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the domain-area tagging of skill-seeds.json: every skill carries an "area" property whose
/// value comes from the closed set below. The area groups skills by business domain and is the axis
/// the knowledge-vs-action overview is generated from, so a new skill without an area would silently
/// disappear from that overview. The field is metadata only — SkillSeedDefinition does not map it and
/// nothing reaches the database — which is why this gate is the sole thing keeping it complete.
/// Scope: the core definitions file. Feature-plugin seed files ship their own skills and are not
/// covered here, because plugin authors do not know the core area set.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillAreaCoverageTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillsJsonProperty = "skills";
    private const string SkillNameJsonProperty = "name";
    private const string SkillAreaJsonProperty = "area";
    private const string KnowledgeHandlerType = "knowledge-happen";
    private const string HandlerTypeJsonProperty = "handlerType";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly HashSet<string> ValidAreas = new(StringComparer.Ordinal)
    {
        "absence",
        "availability",
        "client",
        "contract",
        "group",
        "inbox",
        "klacksy",
        "navigation",
        "period",
        "schedule",
        "security",
        "settings",
        "shift",
        "system",
        "wizard",
    };

    public static IEnumerable<TestCaseData> AllSeedSkills()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        foreach (var skill in document.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
        {
            var name = skill.GetProperty(SkillNameJsonProperty).GetString()!;
            var area = skill.TryGetProperty(SkillAreaJsonProperty, out var areaElement)
                ? areaElement.GetString()
                : null;

            yield return new TestCaseData(name, area).SetName($"Skill_{name}_HasValidArea");
        }
    }

    [TestCaseSource(nameof(AllSeedSkills))]
    public void EverySeedSkill_MustCarryAValidArea(string skillName, string? area)
    {
        area.ShouldNotBeNullOrWhiteSpace(
            $"Skill '{skillName}' has no \"area\" property. Every skill must be assigned to a business " +
            $"domain so it appears in the knowledge-vs-action overview. Valid areas: " +
            $"{string.Join(", ", ValidAreas.OrderBy(a => a, StringComparer.Ordinal))}.");

        ValidAreas.Contains(area!).ShouldBeTrue(
            $"Skill '{skillName}' declares area '{area}', which is not in the closed set. " +
            $"Either fix the value or extend ValidAreas here — a new area is a deliberate decision, " +
            $"not a typo. Valid areas: " +
            $"{string.Join(", ", ValidAreas.OrderBy(a => a, StringComparer.Ordinal))}.");
    }

    [Test]
    public void EveryArea_MustContainAtLeastOneSkill()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        var usedAreas = document.RootElement
            .GetProperty(SkillsJsonProperty)
            .EnumerateArray()
            .Where(s => s.TryGetProperty(SkillAreaJsonProperty, out _))
            .Select(s => s.GetProperty(SkillAreaJsonProperty).GetString())
            .Where(a => a != null)
            .ToHashSet(StringComparer.Ordinal);

        var orphaned = ValidAreas.Except(usedAreas!, StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

        orphaned.ShouldBeEmpty(
            $"These areas are declared valid but no skill uses them: {string.Join(", ", orphaned)}. " +
            "Remove them from ValidAreas, or the closed set drifts away from reality.");
    }

    [Test]
    public void KnowledgeSkills_MustSpreadAcrossAreas_AndBlindAreasAreVisible()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        var knowledgeAreas = document.RootElement
            .GetProperty(SkillsJsonProperty)
            .EnumerateArray()
            .Where(s => s.TryGetProperty(HandlerTypeJsonProperty, out var handler)
                        && handler.ValueKind == JsonValueKind.String
                        && handler.GetString() == KnowledgeHandlerType)
            .Select(s => s.GetProperty(SkillAreaJsonProperty).GetString())
            .ToHashSet(StringComparer.Ordinal);

        knowledgeAreas.ShouldNotBeEmpty(
            "No skill carries handlerType 'knowledge-happen' any more — the knowledge layer would be gone.");

        TestContext.Out.WriteLine(
            "Areas without any knowledge skill: " +
            string.Join(", ", ValidAreas.Except(knowledgeAreas!, StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal)));
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
