// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the parameter schemas in skill-seeds.json so the runtime registry never has to
/// silently degrade again: every declared type must be a real <see cref="SkillParameterType"/>
/// member, enum values require the Enum type, and a required parameter must not carry a
/// defaultValue (it would be dead weight and hides that the model is expected to fill the slot).
/// </summary>

using System.Text.Json;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedParameterSchemaTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly HashSet<string> ValidParameterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "String", "Integer", "Decimal", "Boolean", "Date", "Time", "DateTime", "Array", "Object", "Enum"
    };

    [Test]
    public void Parameters_MustDeclareOnlyKnownTypes()
    {
        var violations = new List<string>();

        foreach (var (skillName, parameter) in EnumerateParameters())
        {
            var type = parameter.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(type) || !ValidParameterTypes.Contains(type))
            {
                var parameterName = parameter.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? "?"
                    : "?";
                violations.Add($"{skillName}/{parameterName}: unknown parameter type '{type}'");
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} contains parameters with unknown types. Valid types: " +
            $"{string.Join(", ", ValidParameterTypes.OrderBy(t => t, StringComparer.Ordinal))}. Offenders: " +
            string.Join(" | ", violations));
    }

    [Test]
    public void Parameters_EnumValuesImplyEnumType()
    {
        var violations = new List<string>();

        foreach (var (skillName, parameter) in EnumerateParameters())
        {
            var hasEnumValues = parameter.TryGetProperty("enumValues", out var enumValues)
                && enumValues.ValueKind == JsonValueKind.Array
                && enumValues.GetArrayLength() > 0;
            if (!hasEnumValues)
            {
                continue;
            }

            var type = parameter.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(type, "Enum", StringComparison.OrdinalIgnoreCase))
            {
                var parameterName = parameter.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? "?"
                    : "?";
                violations.Add($"{skillName}/{parameterName}: enumValues declared but type is '{type}', not 'Enum'");
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} contains parameters whose enumValues imply Enum semantics. " +
            "Offenders: " + string.Join(" | ", violations));
    }

    [Test]
    public void Parameters_RequiredMustNotDeclareDefaultValue()
    {
        var violations = new List<string>();

        foreach (var (skillName, parameter) in EnumerateParameters())
        {
            var isRequired = parameter.TryGetProperty("required", out var required)
                && required.ValueKind == JsonValueKind.True;
            var hasDefault = parameter.TryGetProperty("defaultValue", out var defaultValue)
                && defaultValue.ValueKind != JsonValueKind.Null;

            if (isRequired && hasDefault)
            {
                var parameterName = parameter.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? "?"
                    : "?";
                violations.Add($"{skillName}/{parameterName}: required parameter declares a defaultValue");
            }
        }

        violations.ShouldBeEmpty(
            $"{SkillSeedsFileName} contains required parameters with a defaultValue; a required slot " +
            "must be filled by the model. Offenders: " + string.Join(" | ", violations));
    }

    private static IEnumerable<(string SkillName, JsonElement Parameter)> EnumerateParameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var skillName = skill.GetProperty("name").GetString() ?? string.Empty;

            if (!skill.TryGetProperty("parameters", out var parameters)
                || parameters.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var parameter in parameters.EnumerateArray())
            {
                yield return (skillName, parameter);
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
