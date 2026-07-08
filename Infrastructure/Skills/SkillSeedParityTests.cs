// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Parity gate between skill-seeds.json and the executable skill surface. Forward direction: every
/// enabled seed skill must be resolvable through one of the execution mechanisms the runtime supports
/// (a [SkillImplementation] class compiled into Klacks.Api — hand-written or generated from
/// settings-reader-skills.json —, a generic handler type with a non-empty handler config, or a
/// declarative UiAction whose steps the frontend executes). Reverse direction: every
/// [SkillImplementation] class compiled into Klacks.Api must have a matching entry in skill-seeds.json
/// (or a feature-plugin skill-seeds.json), so no implementation can silently become unreachable dead
/// code in either direction. A third gate enforces that generic-handler configs are authored as JSON
/// objects, because a JSON-encoded string dispatches but fails deserialization at runtime.
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillSeedParityTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string StepsPropertyName = "steps";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    private static readonly HashSet<string> GenericHandlerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        HandlerTypes.GenericList,
        HandlerTypes.GenericDelete,
        HandlerTypes.KnowledgeHappen
    };

    /// <summary>
    /// UiAction skills whose handler config ships intentionally without steps because the frontend
    /// intercepts the function call by name (chat-function-execution.service.ts) instead of running
    /// declarative steps. start_guided_tour triggers the onboarding tour via OnboardingService.
    /// </summary>
    private static readonly HashSet<string> FrontendInterceptedUiActionSkills = new(StringComparer.Ordinal)
    {
        "start_guided_tour"
    };

    /// <summary>
    /// [SkillImplementation] classes that are compiled but intentionally have no seed entry.
    /// Must stay empty — a class without a seed entry is unreachable at runtime; fix the seed
    /// instead of extending this list.
    /// </summary>
    private static readonly HashSet<string> SeedlessImplementationAllowlist = new(StringComparer.Ordinal);

    /// <summary>
    /// Generic-handler skills allowed to carry a JSON-encoded STRING handlerConfig. Must stay
    /// empty — GenericSkillDispatcher cannot deserialize a string token, so such skills error at
    /// runtime; author handlerConfig values as JSON objects instead of extending this list.
    /// </summary>
    private static readonly HashSet<string> DoubleEncodedHandlerConfigAllowlist = new(StringComparer.Ordinal);

    private sealed record SeedSkill(
        string Name,
        bool IsEnabled,
        string? ExecutionType,
        string? HandlerType,
        JsonElement HandlerConfig,
        bool HasHandlerConfig);

    [Test]
    public void EveryEnabledSeedSkill_MustBeResolvableByAnExecutionMechanism()
    {
        var implementationNames = ScanImplementationSkillNames();
        var violations = new List<string>();

        foreach (var skill in LoadSeedSkills().Where(s => s.IsEnabled))
        {
            if (implementationNames.Contains(skill.Name))
            {
                continue;
            }

            if (IsGenericHandlerSkill(skill))
            {
                continue;
            }

            if (IsFrontendExecutedSkill(skill))
            {
                continue;
            }

            violations.Add(
                $"{skill.Name} (executionType={skill.ExecutionType}, handlerType={skill.HandlerType ?? "null"})");
        }

        violations.ShouldBeEmpty(
            $"Every enabled skill in {SkillSeedsFileName} must be resolvable: a [SkillImplementation] " +
            "class (hand-written or generated from settings-reader-skills.json), a generic handler type " +
            "with a non-empty handlerConfig, or a frontend-executed UiAction with declarative steps. " +
            "Unresolvable skills are offered to the model but fail at execution time. Violations: " +
            string.Join("; ", violations));
    }

    [Test]
    public void EveryImplementationClass_MustHaveASeedEntry()
    {
        var knownSkillNames = LoadSeedSkills().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        knownSkillNames.UnionWith(LoadPluginSeedSkillNames());

        var violations = ScanImplementationSkillNames()
            .Where(name => !knownSkillNames.Contains(name))
            .Where(name => !SeedlessImplementationAllowlist.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "Every [SkillImplementation] class compiled into Klacks.Api must have a matching entry in " +
            $"{SkillSeedsFileName} (or a feature-plugin skill-seeds.json); without a seed entry the class " +
            "never reaches the skill registry and is dead code. Violations: " +
            string.Join(", ", violations));
    }

    [Test]
    public void GenericHandlerConfigs_MustBeJsonObjects()
    {
        var violations = LoadSeedSkills()
            .Where(s => s.IsEnabled)
            .Where(s => s.HandlerType != null && GenericHandlerTypes.Contains(s.HandlerType))
            .Where(s => !DoubleEncodedHandlerConfigAllowlist.Contains(s.Name))
            .Where(s => !s.HasHandlerConfig ||
                        s.HandlerConfig.ValueKind != JsonValueKind.Object ||
                        !s.HandlerConfig.EnumerateObject().Any())
            .Select(s => $"{s.Name} (handlerType={s.HandlerType}, configKind={(s.HasHandlerConfig ? s.HandlerConfig.ValueKind.ToString() : "missing")})")
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        violations.ShouldBeEmpty(
            "handlerConfig of a generic-handler skill must be a non-empty JSON OBJECT. A JSON-encoded " +
            "string passes the dispatch check but fails deserialization in GenericSkillDispatcher, so the " +
            "skill errors at runtime. Violations: " + string.Join("; ", violations));
    }

    private static bool IsGenericHandlerSkill(SeedSkill skill)
    {
        if (skill.HandlerType == null || !GenericHandlerTypes.Contains(skill.HandlerType))
        {
            return false;
        }

        if (!skill.HasHandlerConfig)
        {
            return false;
        }

        if (skill.HandlerConfig.ValueKind == JsonValueKind.Object)
        {
            return skill.HandlerConfig.EnumerateObject().Any();
        }

        return skill.HandlerConfig.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(skill.HandlerConfig.GetString());
    }

    private static bool IsFrontendExecutedSkill(SeedSkill skill)
    {
        if (skill.ExecutionType == LlmExecutionTypes.UiPassthrough)
        {
            return true;
        }

        if (skill.ExecutionType != LlmExecutionTypes.UiAction)
        {
            return false;
        }

        if (FrontendInterceptedUiActionSkills.Contains(skill.Name))
        {
            return true;
        }

        return skill.HasHandlerConfig &&
               skill.HandlerConfig.ValueKind == JsonValueKind.Object &&
               skill.HandlerConfig.TryGetProperty(StepsPropertyName, out var steps) &&
               steps.ValueKind == JsonValueKind.Array &&
               steps.GetArrayLength() > 0;
    }

    private static HashSet<string> ScanImplementationSkillNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in GetLoadableTypes(typeof(SkillRegistryInitializer).Assembly).Where(t => !t.IsAbstract))
        {
            var coreAttribute = type.GetCustomAttribute<Klacks.Api.Domain.Attributes.SkillImplementationAttribute>();
            if (coreAttribute != null)
            {
                names.Add(coreAttribute.SkillName);
                continue;
            }

            var contractAttribute = type.GetCustomAttribute<Klacks.Plugin.Contracts.Skills.SkillImplementationAttribute>();
            if (contractAttribute != null)
            {
                names.Add(contractAttribute.SkillName);
            }
        }

        return names;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null).Select(t => t!);
        }
    }

    private static List<SeedSkill> LoadSeedSkills()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(SkillSeedsFileName)));
        var skills = new List<SeedSkill>();

        foreach (var element in document.RootElement.GetProperty("skills").EnumerateArray())
        {
            var hasHandlerConfig = element.TryGetProperty("handlerConfig", out var handlerConfig) &&
                                   handlerConfig.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

            skills.Add(new SeedSkill(
                Name: element.GetProperty("name").GetString() ?? string.Empty,
                IsEnabled: !element.TryGetProperty("isEnabled", out var enabled) || enabled.GetBoolean(),
                ExecutionType: element.TryGetProperty("executionType", out var et) ? et.GetString() : null,
                HandlerType: element.TryGetProperty("handlerType", out var ht) ? ht.GetString() : null,
                HandlerConfig: hasHandlerConfig ? handlerConfig.Clone() : default,
                HasHandlerConfig: hasHandlerConfig));
        }

        return skills;
    }

    private static IEnumerable<string> LoadPluginSeedSkillNames()
    {
        var featuresDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (featuresDir == null)
        {
            yield break;
        }

        foreach (var pluginDir in Directory.GetDirectories(featuresDir))
        {
            var seedFile = Path.Combine(pluginDir, SkillSeedsFileName);
            if (!File.Exists(seedFile))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(seedFile));
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var name = element.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name;
                }
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
