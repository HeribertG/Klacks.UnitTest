// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W3 gates for description and parameter-shape hygiene in skill-seeds.json:
/// descriptions stay between 40 and 500 characters (the explain_page_* knowledge skills may run to
/// 1000), every parameter description is at least 15 characters so the model gets a real hint for
/// each slot, and no skill outgrows 12 parameters without an explicitly justified allowlist entry.
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedDescriptionQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
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
