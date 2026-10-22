// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shared source and seed scan for the architecture guards that restrict fuzzy name matching to read-only skills
/// (NameMatchPolicyLenientGuardTests, ClientFuzzySearchGuardTests): which Api files mention a marker outside line
/// comments, which skill names a type implements (SkillImplementationAttribute; file name = type name), the seeded
/// effect of every skill (skill-seeds.json, missing effect = AgentSkillDefaults.Effect) and which skills a seeded
/// recipe captures an id from (recipe-seeds.json).
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Domain.Attributes;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.TestHelpers;

public static class SkillGuardSources
{
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const string BinDirectory = "bin";
    private const string ObjDirectory = "obj";
    private const string MigrationsDirectory = "Migrations";

    public static List<string> FilesContaining(string marker)
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            var firstSegment = relative.Split('/')[0];
            if (firstSegment is BinDirectory or ObjDirectory or MigrationsDirectory)
            {
                continue;
            }

            var mentions = File.ReadLines(file)
                .Any(line => !line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal)
                             && line.Contains(marker, StringComparison.Ordinal));
            if (mentions)
            {
                hits.Add(relative);
            }
        }

        return hits;
    }

    public static string TypeNameOf(string relativeFile) => Path.GetFileNameWithoutExtension(relativeFile);

    public static string? DescribeNonReadSkill(
        string file,
        IReadOnlyDictionary<string, List<string>> skillNamesByType,
        IReadOnlyDictionary<string, SkillEffect> effects)
    {
        if (!skillNamesByType.TryGetValue(TypeNameOf(file), out var skillNames))
        {
            return $"{file} is neither a skill nor allowlisted";
        }

        var mutating = skillNames
            .Where(name => effects.GetValueOrDefault(name, AgentSkillDefaults.Effect) == SkillEffect.Mutate)
            .ToList();
        return mutating.Count == 0
            ? null
            : $"{file} implements the mutating skill(s) {string.Join(", ", mutating)}";
    }

    public static string? DescribeCapturingSkill(
        string file,
        IReadOnlyDictionary<string, List<string>> skillNamesByType,
        IReadOnlySet<string> capturingSkills)
    {
        var captured = (skillNamesByType.GetValueOrDefault(TypeNameOf(file)) ?? [])
            .Where(capturingSkills.Contains)
            .ToList();
        return captured.Count == 0
            ? null
            : $"{file} implements the recipe-captured skill(s) {string.Join(", ", captured)}";
    }

    public static Dictionary<string, SkillEffect> LoadSkillEffects()
    {
        var seedPath = RepositoryRootLocator.RequireFile(
            [RepositoryRootLocator.ApiProjectDirectoryName, "Application", "Skills", "Definitions", "skill-seeds.json"]);
        using var document = JsonDocument.Parse(File.ReadAllText(seedPath));

        var effects = new Dictionary<string, SkillEffect>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var name = skill.GetProperty("name").GetString()!;
            effects[name] = skill.TryGetProperty("effect", out var effect)
                            && effect.ValueKind == JsonValueKind.String
                            && Enum.TryParse<SkillEffect>(effect.GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : AgentSkillDefaults.Effect;
        }

        effects.ShouldNotBeEmpty("skill-seeds.json yielded no skills.");
        return effects;
    }

    public static HashSet<string> LoadCapturingRecipeSkills()
    {
        var seedPath = RepositoryRootLocator.RequireFile(
            [RepositoryRootLocator.ApiProjectDirectoryName, "Application", "Skills", "Definitions", "recipe-seeds.json"]);
        using var document = JsonDocument.Parse(File.ReadAllText(seedPath));

        var capturing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            if (!recipe.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var step in steps.EnumerateArray())
            {
                if (step.TryGetProperty("capture", out var capture)
                    && capture.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(capture.GetString())
                    && step.TryGetProperty("skill", out var skill)
                    && skill.ValueKind == JsonValueKind.String)
                {
                    capturing.Add(skill.GetString()!);
                }
            }
        }

        return capturing;
    }

    public static Dictionary<string, List<string>> LoadSkillNamesByTypeName()
    {
        return LoadableTypes(typeof(SkillImplementationAttribute).Assembly)
            .Select(type => (type.Name, Names: type.GetCustomAttributes<SkillImplementationAttribute>(inherit: false)
                .Select(attribute => attribute.SkillName)
                .ToList()))
            .Where(entry => entry.Names.Count > 0)
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(entry => entry.Names).ToList(), StringComparer.Ordinal);
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).Select(type => type!);
        }
    }
}
