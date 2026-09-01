// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W3 gates for description and parameter-shape hygiene in skill-seeds.json:
/// descriptions stay between 40 and 500 characters (the explain_page_* knowledge skills may run to
/// 1000), every parameter description is at least 15 characters so the model gets a real hint for
/// each slot, and no skill outgrows 12 parameters without an explicitly justified allowlist entry.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedDescriptionQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SkillRelationSeedsFileName = "skill-relation-seeds.json";
    private const int DescriptionMinLength = 40;
    private const int DescriptionMaxLength = 500;
    private const int KnowledgePageDescriptionMaxLength = 1000;
    private const int ParameterDescriptionMinLength = 15;
    private const int ParameterCountMax = 12;

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    /// <summary>
    /// Form-heavy creation/update skills that legitimately need more than 12 parameters. Splitting
    /// them into sub-skills is UI work; until then they are allowlisted here on purpose.
    /// </summary>
    private static readonly HashSet<string> ParameterCountAllowlist = new(StringComparer.Ordinal)
    {
        "create_contract", "create_employee", "update_scheduling_defaults", "create_scheduling_rule",
        "create_shift", "update_client", "update_qualification", "update_contract",
        "create_identity_provider", "update_identity_provider", "update_compliance_enforcement_settings",
        "update_surcharge_mode_settings", "update_grid_color_settings", "update_speech_settings"
    };

    [Test]
    public void Descriptions_MustBeWithinBounds()
    {
        var violations = new List<string>();

        foreach (var (skillName, description) in EnumerateDescriptions())
        {
            var max = skillName.StartsWith("explain_page_", StringComparison.Ordinal)
                ? KnowledgePageDescriptionMaxLength
                : DescriptionMaxLength;

            if (description.Length < DescriptionMinLength || description.Length > max)
            {
                violations.Add($"{skillName}: {description.Length} chars");
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} descriptions must be {DescriptionMinLength}-{DescriptionMaxLength} chars " +
            $"({KnowledgePageDescriptionMaxLength} for explain_page_*). Offenders: " +
            string.Join(" | ", violations));
    }

    /// <summary>
    /// W3.2: a description may only name another skill when a relation between the two skills exists
    /// in the skill graph (learned or curated). Free name-drops bypass skill-discoverability §4 and
    /// would lock a cross-reference into the LLM context without any graph edge to back it.
    /// </summary>
    [Test]
    public void Descriptions_MustOnlyNameSkills_WithExistingRelation()
    {
        var skillNames = EnumerateSkillNames();
        var relationPairs = LoadRelationPairs();

        var violations = new List<string>();

        foreach (var (skillName, description) in EnumerateDescriptions())
        {
            foreach (var other in skillNames)
            {
                if (string.Equals(skillName, other, StringComparison.Ordinal))
                {
                    continue;
                }

                if (Regex.IsMatch(description, $@"\b{Regex.Escape(other)}\b", RegexOptions.IgnoreCase)
                    && !relationPairs.Contains((skillName, other)))
                {
                    violations.Add($"{skillName} mentions {other}");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} descriptions may only mention another skill when a relation " +
            $"connects the two skills in {SkillRelationSeedsFileName}. Offenders: " + string.Join(" | ", violations));
    }

    [Test]
    public void ParameterDescriptions_MustBeAtLeast15Characters()
    {
        var violations = new List<string>();

        foreach (var (skillName, parameter) in EnumerateParameters())
        {
            var description = parameter.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;

            if (description.Length < ParameterDescriptionMinLength)
            {
                var parameterName = parameter.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? "?"
                    : "?";
                violations.Add($"{skillName}/{parameterName}: '{description}' ({description.Length} chars)");
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} parameter descriptions must be at least " +
            $"{ParameterDescriptionMinLength} characters. Offenders: " + string.Join(" | ", violations));
    }

    [Test]
    public void Skills_MustNotExceedParameterCount_WithoutAllowlistEntry()
    {
        var violations = new List<string>();

        foreach (var (skillName, parameters) in EnumerateSkillParameters())
        {
            if (parameters.Count > ParameterCountMax && !ParameterCountAllowlist.Contains(skillName))
            {
                violations.Add($"{skillName}: {parameters.Count} parameters");
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} skills must declare at most {ParameterCountMax} parameters. " +
            "Form-heavy skills belong in the allowlist with a justification. Offenders: " +
            string.Join(" | ", violations));
    }

    private static HashSet<string> EnumerateSkillNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = skill.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static HashSet<(string A, string B)> LoadRelationPairs()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillRelationSeedsFileName)));
        var pairs = new HashSet<(string A, string B)>();
        foreach (var relation in document.RootElement.GetProperty("relations").EnumerateArray())
        {
            var a = relation.TryGetProperty("skillAName", out var aElement) ? aElement.GetString() ?? string.Empty : string.Empty;
            var b = relation.TryGetProperty("skillBName", out var bElement) ? bElement.GetString() ?? string.Empty : string.Empty;
            pairs.Add((a, b));
            pairs.Add((b, a));
        }

        return pairs;
    }

    private static IEnumerable<(string SkillName, string Description)> EnumerateDescriptions()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = skill.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var description = skill.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString() ?? string.Empty
                : string.Empty;
            yield return (name, description);
        }
    }

    private static IEnumerable<(string SkillName, JsonElement Parameter)> EnumerateParameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = skill.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            if (!skill.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var parameter in parameters.EnumerateArray())
            {
                yield return (name, parameter);
            }
        }
    }

    private static IEnumerable<(string SkillName, List<JsonElement> Parameters)> EnumerateSkillParameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = skill.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
            var parameters = skill.TryGetProperty("parameters", out var parametersElement)
                && parametersElement.ValueKind == JsonValueKind.Array
                ? parametersElement.EnumerateArray().ToList()
                : new List<JsonElement>();
            yield return (name, parameters);
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

        throw new FileNotFoundException($"Could not locate {fileName} above {AppContext.BaseDirectory}");
    }
}
