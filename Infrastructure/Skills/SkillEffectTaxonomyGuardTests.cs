// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Consistency gate for the skill effect taxonomy (Explain/Read/Advise/Mutate): every knowledge-happen
/// skill must be Explain, every Advise skill must have a curated AdvisesFor edge to the act skill it
/// recommends, and no Mutate skill may be force-injected into every toolset (alwaysOn). Reads
/// skill-seeds.json (core and feature-plugin) and skill-relation-seeds.json directly from disk, mirroring
/// SkillSeedParityTests / SkillRelationSeedParityTests.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillEffectTaxonomyGuardTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string RelationSeedsFileName = "skill-relation-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record SeedSkill(string Name, string? HandlerType, string? Effect, bool AlwaysOn);

    // Curated, single-item exception to the "no Mutate skill is alwaysOn" rule. confirm_pending_action
    // re-executes a previously withheld, arbitrary action after the user has explicitly confirmed it, so
    // it is factually Mutate-capable — but it must stay alwaysOn=true, otherwise a bare "yes" reply could
    // miss it in retrieval on some turns. This is a deliberate, individually reviewed exception, not a
    // loosening of the general rule: any other Mutate+alwaysOn skill must still fail this test.
    private static readonly HashSet<string> AlwaysOnMutateExceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        AutonomyDefaults.ConfirmPendingActionSkillName,
    };

    [Test]
    public void EveryKnowledgeHappenSkill_MustHaveExplainEffect()
    {
        var violations = LoadAllSeedSkills()
            .Where(s => string.Equals(s.HandlerType, HandlerTypes.KnowledgeHappen, StringComparison.OrdinalIgnoreCase))
            .Where(s => !string.Equals(s.Effect, nameof(SkillEffect.Explain), StringComparison.OrdinalIgnoreCase))
            .Select(s => $"{s.Name} (effect={s.Effect ?? "null"})")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            $"Every skill with handlerType={HandlerTypes.KnowledgeHappen} must carry effect=" +
            $"{nameof(SkillEffect.Explain)} in skill-seeds.json. Violations: " + string.Join(", ", violations));
    }

    [Test]
    public void EveryAdviseSkill_HasAtLeastOneAdvisesForEdge()
    {
        var adviseSkillNames = LoadAllSeedSkills()
            .Where(s => string.Equals(s.Effect, nameof(SkillEffect.Advise), StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);

        var advisesForSources = LoadRelationSeeds()
            .Where(r => r.Type == SkillRelationType.AdvisesFor)
            .Select(r => r.SkillAName)
            .ToHashSet(StringComparer.Ordinal);

        var violations = adviseSkillNames
            .Where(name => !advisesForSources.Contains(name))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            $"Every skill with effect={nameof(SkillEffect.Advise)} must be the source of at least one " +
            $"{nameof(SkillRelationType.AdvisesFor)} edge in skill-relation-seeds.json. Violations: " +
            string.Join(", ", violations));
    }

    [Test]
    public void NoMutateSkill_IsAlwaysOn()
    {
        var violations = LoadAllSeedSkills()
            .Where(s => ParseEffectFailClosed(s.Effect) == SkillEffect.Mutate)
            .Where(s => s.AlwaysOn)
            .Where(s => !AlwaysOnMutateExceptions.Contains(s.Name))
            .Select(s => s.Name)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            $"A skill with effect={nameof(SkillEffect.Mutate)} — including fail-closed, e.g. a missing or " +
            "unparseable effect field — must be found deliberately through retrieval, not force-injected into " +
            "every toolset via alwaysOn=true. Violations: " + string.Join(", ", violations));
    }

    [Test]
    public void EverySeedSkill_HasParseableEffect()
    {
        var validNames = Enum.GetNames(typeof(SkillEffect));

        var violations = LoadAllSeedSkills()
            .Where(s => !IsKnownEffectName(s.Effect, validNames))
            .Select(s => $"{s.Name} (effect={s.Effect ?? "null"})")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "Every seed skill must carry an effect field that parses case-insensitively to a known " +
            $"{nameof(SkillEffect)} value (Explain/Read/Advise/Mutate) — not null, not empty, no typo. " +
            "Violations: " + string.Join(", ", violations));
    }

    private static bool IsKnownEffectName(string? effect, string[] validNames)
    {
        return !string.IsNullOrWhiteSpace(effect)
            && validNames.Any(name => string.Equals(name, effect, StringComparison.OrdinalIgnoreCase));
    }

    // Mirrors SkillSeedLoader.ParseEffect exactly: null/whitespace/unparseable falls back to
    // AgentSkillDefaults.Effect (Mutate), the fail-closed runtime value — not the raw JSON string.
    private static SkillEffect ParseEffectFailClosed(string? effect)
    {
        if (!string.IsNullOrWhiteSpace(effect) && Enum.TryParse<SkillEffect>(effect, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return AgentSkillDefaults.Effect;
    }

    private static List<SeedSkill> LoadAllSeedSkills()
    {
        var skills = new List<SeedSkill>();
        AddSeedSkillsFrom(LocateDefinitionsFile(SkillSeedsFileName), skills);

        var pluginsDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (pluginsDir != null)
        {
            foreach (var pluginDir in Directory.GetDirectories(pluginsDir))
            {
                var pluginSeedFile = Path.Combine(pluginDir, SkillSeedsFileName);
                if (File.Exists(pluginSeedFile))
                {
                    AddSeedSkillsFrom(pluginSeedFile, skills);
                }
            }
        }

        return skills;
    }

    private static void AddSeedSkillsFrom(string filePath, List<SeedSkill> skills)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        var skillsElement = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.GetProperty("skills");

        foreach (var element in skillsElement.EnumerateArray())
        {
            skills.Add(new SeedSkill(
                Name: element.GetProperty("name").GetString() ?? string.Empty,
                HandlerType: element.TryGetProperty("handlerType", out var ht) ? ht.GetString() : null,
                Effect: element.TryGetProperty("effect", out var ef) ? ef.GetString() : null,
                AlwaysOn: element.TryGetProperty("alwaysOn", out var ao) && ao.GetBoolean()));
        }
    }

    private static List<SkillRelationSeedDefinition> LoadRelationSeeds()
    {
        var path = LocateDefinitionsFile(RelationSeedsFileName);
        var seedFile = JsonSerializer.Deserialize<SkillRelationSeedFile>(File.ReadAllText(path), JsonReadOptions);
        seedFile.ShouldNotBeNull();
        return seedFile!.Relations;
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

    private static string? TryLocateDir(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
