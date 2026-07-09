// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnGoldsetQualityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const int ExpectedVersion = 2;
    private const string ExpectedKind = "turn-selection";
    private const string ClientEntityType = "client";

    private static readonly string[] GoldsetFileNames = ["turn-selection-v1.json", "turn-names-v1.json"];

    private static readonly string[] GoldsetsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Goldsets"
    ];

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Test]
    public void Goldsets_MustDeclareVersionAndKind()
    {
        foreach (var (fileName, document) in LoadGoldsets())
        {
            document.Version.ShouldBe(ExpectedVersion, fileName);
            document.Kind.ShouldBe(ExpectedKind, fileName);
            document.Items.ShouldNotBeEmpty(fileName);
        }
    }

    [Test]
    public void Goldsets_ItemIdsMustBeUnique()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            var duplicates = document.Items
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => $"{fileName}: duplicate item id '{g.Key}'");
            violations.AddRange(duplicates);

            violations.AddRange(document.Items
                .Where(i => string.IsNullOrWhiteSpace(i.Id))
                .Select(_ => $"{fileName}: item with blank id"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ExpectedToolsAndAlternativesMustExistInSkillSeeds()
    {
        var skills = LoadSkillParameters();
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items.Where(i => i.ExpectedTool != null))
            {
                if (!skills.ContainsKey(item.ExpectedTool!))
                {
                    violations.Add($"{fileName}/{item.Id}: expectedTool '{item.ExpectedTool}' not found in {SkillSeedsFileName}");
                }

                violations.AddRange(item.AlternativeTools
                    .Where(alt => !skills.ContainsKey(alt))
                    .Select(alt => $"{fileName}/{item.Id}: alternativeTool '{alt}' not found in {SkillSeedsFileName}"));
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ExpectedSlotsMustBeParametersOfTheSkill()
    {
        var skills = LoadSkillParameters();
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items.Where(i => i.ExpectedTool != null && skills.ContainsKey(i.ExpectedTool!)))
            {
                var parameters = skills[item.ExpectedTool!];
                violations.AddRange(item.ExpectedSlots
                    .Where(slot => !parameters.Contains(slot.Name))
                    .Select(slot =>
                        $"{fileName}/{item.Id}: slot '{slot.Name}' is not a parameter of skill '{item.ExpectedTool}' " +
                        $"(parameters: {string.Join(", ", parameters.Order())})"));
            }
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_NoToolItemsMustNotHaveSlots()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            violations.AddRange(document.Items
                .Where(i => i.ExpectedTool == null && i.ExpectedSlots.Count > 0)
                .Select(i => $"{fileName}/{i.Id}: no-tool item must not define expectedSlots"));
        }

        violations.ShouldBeEmpty();
    }

    [Test]
    public void Goldsets_ResolvedEntitySlotsMustReferenceEntityWithPositiveIdNumber()
    {
        var violations = new List<string>();

        foreach (var (fileName, document) in LoadGoldsets())
        {
            foreach (var item in document.Items)
            {
                foreach (var slot in item.ExpectedSlots.Where(s => s.Match == SlotMatchMode.ResolvedEntityId))
                {
                    if (slot.Entity == null)
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has no entity reference");
                    }
                    else if (slot.Entity.IdNumber <= 0)
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has non-positive idNumber {slot.Entity.IdNumber}");
                    }
                    else if (string.IsNullOrWhiteSpace(slot.Entity.Type))
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' has blank entity type");
                    }
                    else if (!string.Equals(slot.Entity.Type, ClientEntityType, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"{fileName}/{item.Id}: resolved-entity-id slot '{slot.Name}' uses unsupported entity type '{slot.Entity.Type}'");
                    }
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    private static List<(string FileName, TurnGoldsetDocument Document)> LoadGoldsets()
    {
        return GoldsetFileNames
            .Select(fileName =>
            {
                var path = LocateRepoFile(GoldsetsRelativePath, fileName);
                var document = JsonSerializer.Deserialize<TurnGoldsetDocument>(File.ReadAllText(path), SerializerOptions);
                document.ShouldNotBeNull(fileName);
                return (fileName, document!);
            })
            .ToList();
    }

    private static Dictionary<string, HashSet<string>> LoadSkillParameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateRepoFile(DefinitionsRelativePath, SkillSeedsFileName)));
        var skills = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var parameters = new HashSet<string>(StringComparer.Ordinal);

            if (skill.TryGetProperty("parameters", out var parameterArray)
                && parameterArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in parameterArray.EnumerateArray())
                {
                    if (parameter.TryGetProperty("name", out var parameterName)
                        && parameterName.ValueKind == JsonValueKind.String)
                    {
                        parameters.Add(parameterName.GetString()!);
                    }
                }
            }

            skills[name] = parameters;
        }

        return skills;
    }

    private static string LocateRepoFile(string[] relativePath, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath, fileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', relativePath)}/{fileName} by walking up from the test base directory.");
    }
}
