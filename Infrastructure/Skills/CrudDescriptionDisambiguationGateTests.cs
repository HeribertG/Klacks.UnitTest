// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W4 gate: locks the CRUD disambiguation that the first measurement round made necessary.
/// Calibration showed create_* -> list_*, update_*_settings -> get_*_settings and delete_* -> list_*
/// as the dominant miss cluster. Every mutating CRUD skill whose read counterpart exists must carry a
/// "Do NOT use when …" boundary sentence, and so must the read counterpart. Kept deliberately narrow:
/// only pairs where both sides exist in the seed file.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Skills;

using System.Text.Json;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class CrudDescriptionDisambiguationGateTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] MutatingPrefixes =
    [
        "create_", "add_", "update_", "delete_", "remove_", "set_", "assign_",
        "enable_", "disable_", "install_", "uninstall_", "revoke_", "reset_",
        "accept_", "dismiss_", "apply_", "cancel_", "start_", "send_", "move_", "mark_", "clear_"
    ];

    private static readonly string[] ReadPrefixes = ["list_", "get_"];

    [Test]
    public void CrudSkillsWithReadCounterpart_MustDisambiguateWithDoNotUseSentence()
    {
        var violations = new List<string>();

        using var document = JsonDocument.Parse(File.ReadAllText(LocateSeeds()));
        var skills = document.RootElement.GetProperty("skills").EnumerateArray().ToList();
        var names = skills.Select(s => s.GetProperty("name").GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);

        foreach (var skill in skills)
        {
            var name = skill.GetProperty("name").GetString() ?? string.Empty;
            var effect = skill.TryGetProperty("effect", out var effectProperty) && effectProperty.ValueKind == JsonValueKind.String
                ? effectProperty.GetString()
                : null;
            var description = skill.TryGetProperty("description", out var descriptionProperty) && descriptionProperty.ValueKind == JsonValueKind.String
                ? descriptionProperty.GetString() ?? string.Empty
                : string.Empty;

            if (effect == "Mutate" && MutatingPrefixes.Any(name.StartsWith))
            {
                var counterpart = FindReadCounterpart(name, names);
                if (counterpart != null && !description.Contains("do not use", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{name}: mutating CRUD skill with read counterpart '{counterpart}' lacks a 'do NOT use' sentence");
                }
            }

            if (ReadPrefixes.Any(name.StartsWith))
            {
                var mutatingSibling = FindMutatingSibling(name, names, skills);
                if (mutatingSibling != null && !description.Contains("do not use", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{name}: read CRUD skill with mutating sibling '{mutatingSibling}' lacks a 'do NOT use' sentence");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    private static string? FindReadCounterpart(string mutatingName, HashSet<string> names)
    {
        var prefix = MutatingPrefixes.First(mutatingName.StartsWith);
        var suffix = mutatingName[prefix.Length..];

        foreach (var readPrefix in ReadPrefixes)
        {
            foreach (var variant in SuffixVariants(suffix))
            {
                var candidate = readPrefix + variant;
                if (names.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindMutatingSibling(string readName, HashSet<string> names, List<JsonElement> skills)
    {
        var prefix = ReadPrefixes.First(readName.StartsWith);
        var suffix = readName[prefix.Length..];

        foreach (var mutatingPrefix in MutatingPrefixes)
        {
            foreach (var variant in SuffixVariants(suffix))
            {
                var candidate = mutatingPrefix + variant;
                if (!names.Contains(candidate))
                {
                    continue;
                }

                var effect = skills
                    .Where(s => (s.GetProperty("name").GetString() ?? string.Empty) == candidate)
                    .Select(s => s.TryGetProperty("effect", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                    .FirstOrDefault();

                if (effect == "Mutate")
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SuffixVariants(string suffix)
    {
        yield return suffix;
        yield return suffix + "s";

        if (suffix.EndsWith('s'))
        {
            yield return suffix[..^1];
        }
    }

    private static string LocateSeeds()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine([dir.FullName, .. DefinitionsRelativePath, SkillSeedsFileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{SkillSeedsFileName} by walking up from the test base directory.");
    }
}
