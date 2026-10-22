// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the by-name resolution policy. NameResolution and every entity resolver default to
/// NameMatchPolicy.Strict, which never acts on a typo, token or phonetic match ("Region South" must not
/// silently become the group "Region North"); a write skill gets that protection without any code. The
/// Lenient policy keeps the fuzzy stages and may only be passed where nothing is written:
/// - a file that names NameMatchPolicy.Lenient must be a skill whose seeded effect (skill-seeds.json,
///   missing effect = AgentSkillDefaults.Effect = Mutate) is not Mutate, or be on the explicit allowlist below;
/// - such a skill must not be a capturing step of any seeded recipe (recipe-seeds.json), because a captured
///   id is injected into the next, writing step without the user ever seeing the fuzzy match;
/// - the allowlisted services that pass Lenient on behalf of their callers (the holiday calendar target
///   resolver, the contract reference resolver in WithFuzzy mode, the group search repository) may only be
///   consumed by such skills; the one exception is open_schedule, seeded as Mutate although it only opens the
///   schedule page of the found group (navigation, nothing is stored), see ConsumerExceptions.
/// A source scan is used because the policy is an argument at a call site, which reflection cannot see; the
/// skill name and effect are then resolved by reflection over SkillImplementationAttribute (file name = type
/// name, enforced by OneTypePerFileGuardTests) and the seed file.
/// Not covered: GroupRepository's fuzzy fallback is reached only through the group list query of the UI
/// controller and is not consumer-checked (a new non-skill consumer of a checked service fails the guard and has to
/// be listed with a reason); skills seeded only by plugins; and any fuzzy matching that does
/// not go through NameResolution (client search, list_groups' plain substring filter), which this scan cannot
/// see because it only looks for the Lenient policy.
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Domain.Attributes;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class NameMatchPolicyLenientGuardTests
{
    private const string LenientMarker = "NameMatchPolicy.Lenient";
    private const string FuzzyContractModeMarker = "ContractNameMatchMode.WithFuzzy";
    private const string HolidayTargetResolverMarker = "IHolidayCalendarTargetResolver";
    private const string GroupSearchRepositoryMarker = "IGroupSearchRepository";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const string BinDirectory = "bin";
    private const string ObjDirectory = "obj";
    private const string MigrationsDirectory = "Migrations";
    private const int MinimumLenientSkillFiles = 10;

    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.Ordinal)
    {
        ["Application/Skills/NameResolution.cs"] =
            "the resolution mechanism itself, which interprets the policy",
        ["Infrastructure/Repositories/Associations/GroupRepository.cs"] =
            "fuzzy fallback of the UI group list (GetTruncatedListQuery from the controller); intended fuzzy search",
        ["Infrastructure/Repositories/Associations/GroupSearchRepository.cs"] =
            "fuzzy fallback of the group search used for navigation and lookups; intended fuzzy search",
        ["Application/Services/Schedules/HolidayCalendarTargetResolver.cs"] =
            "only consumed by read-only holiday skills, checked by ServicesThatPassLenient_AreOnlyConsumedByNonMutatingSkills",
        ["Application/Services/Contracts/ContractReferenceResolver.cs"] =
            "passes Lenient only for ContractNameMatchMode.WithFuzzy, checked by ServicesThatPassLenient_AreOnlyConsumedByNonMutatingSkills",
    };

    private static readonly string[] ConsumerMarkers =
        [HolidayTargetResolverMarker, FuzzyContractModeMarker, GroupSearchRepositoryMarker];

    private static readonly string[] ConsumerMarkerDefinitionFiles =
    [
        "Application/Interfaces/Schedules/IHolidayCalendarTargetResolver.cs",
        "Application/Services/Schedules/HolidayCalendarTargetResolver.cs",
        "Application/Services/Contracts/ContractReferenceResolver.cs",
        "Application/Interfaces/IGroupSearchRepository.cs",
        "Infrastructure/Repositories/Associations/GroupSearchRepository.cs",
        "Infrastructure/Extensions/ServiceCollectionExtensions.cs",
        "Infrastructure/Extensions/RepositoryServiceCollectionExtensions.cs",
    ];

    private static readonly Dictionary<string, string> ConsumerExceptions = new(StringComparer.Ordinal)
    {
        ["Application/Skills/OpenScheduleSkill.cs"] =
            "open_schedule is seeded as Mutate but only navigates to the schedule of the found group; nothing is stored",
    };

    [Test]
    public void Lenient_IsUsedOnlyByNonMutatingSkillsOrAllowlistedFiles()
    {
        var effects = LoadSkillEffects();
        var skillNamesByType = LoadSkillNamesByTypeName();
        var lenientFiles = FilesContaining(LenientMarker);

        var violations = new List<string>();
        var skillFiles = 0;
        foreach (var file in lenientFiles)
        {
            if (Allowlist.ContainsKey(file))
            {
                continue;
            }

            var problem = DescribeNonReadSkill(file, skillNamesByType, effects);
            if (problem != null)
            {
                violations.Add(problem);
            }
            else
            {
                skillFiles++;
            }
        }

        violations.ShouldBeEmpty(
            $"{LenientMarker} lets the fuzzy stages pick an entity without asking the user. It is only allowed in " +
            "skills whose seeded effect is not Mutate (or in the allowlisted files). Remove it, or fix the " +
            "skill's effect if the skill really only reads. Violations: " + string.Join("; ", violations));

        skillFiles.ShouldBeGreaterThanOrEqualTo(
            MinimumLenientSkillFiles,
            $"Only {skillFiles} read-only skill files pass {LenientMarker}; the scan cannot have read the real sources.");
    }

    [Test]
    public void AllowlistedFiles_StillUseLenient()
    {
        var lenientFiles = FilesContaining(LenientMarker).ToHashSet(StringComparer.Ordinal);

        var stale = Allowlist.Keys.Where(file => !lenientFiles.Contains(file)).ToList();

        stale.ShouldBeEmpty(
            "These allowlist entries no longer pass " + LenientMarker + " and must be removed (shrink-only): " +
            string.Join(", ", stale));
    }

    [Test]
    public void LenientSkills_AreNotCapturingRecipeSteps()
    {
        var skillNamesByType = LoadSkillNamesByTypeName();
        var capturingSkills = LoadCapturingRecipeSkills();
        capturingSkills.ShouldNotBeEmpty("recipe-seeds.json has no capturing step; the scan is not reading the real seeds.");

        var lenientSkillNames = FilesContaining(LenientMarker)
            .Where(file => !Allowlist.ContainsKey(file))
            .SelectMany(file => skillNamesByType.GetValueOrDefault(TypeNameOf(file)) ?? [])
            .ToList();

        var offenders = lenientSkillNames.Where(capturingSkills.Contains).ToList();

        offenders.ShouldBeEmpty(
            "A recipe captures an id from these skills and injects it into a writing step in the same turn, so a " +
            "fuzzy match would be written without the user ever confirming it. Keep them Strict: " +
            string.Join(", ", offenders));
    }

    [Test]
    public void ServicesThatPassLenient_AreOnlyConsumedByNonMutatingSkills()
    {
        var effects = LoadSkillEffects();
        var skillNamesByType = LoadSkillNamesByTypeName();
        var capturingSkills = LoadCapturingRecipeSkills();

        foreach (var marker in ConsumerMarkers)
        {
            var consumers = FilesContaining(marker)
                .Where(file => !ConsumerMarkerDefinitionFiles.Contains(file, StringComparer.Ordinal)
                               && !ConsumerExceptions.ContainsKey(file))
                .ToList();

            consumers.ShouldNotBeEmpty($"No consumer of '{marker}' found; the scan is not reading the real sources.");

            var violations = consumers
                .Select(file => DescribeNonReadSkill(file, skillNamesByType, effects)
                                ?? DescribeCapturingSkill(file, skillNamesByType, capturingSkills))
                .Where(problem => problem != null)
                .ToList();

            violations.ShouldBeEmpty(
                $"'{marker}' resolves names with the Lenient policy, so only non-mutating skills that no recipe " +
                "captures from may use it. Violations: " + string.Join("; ", violations));
        }

        var staleExceptions = ConsumerExceptions.Keys
            .Where(file => !ConsumerMarkers.Any(marker => FilesContaining(marker).Contains(file, StringComparer.Ordinal)))
            .ToList();
        staleExceptions.ShouldBeEmpty(
            "These consumer exceptions no longer use a checked service and must be removed (shrink-only): " +
            string.Join(", ", staleExceptions));
    }

    private static string? DescribeNonReadSkill(
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

    private static string? DescribeCapturingSkill(
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

    private static string TypeNameOf(string relativeFile) => Path.GetFileNameWithoutExtension(relativeFile);

    private static List<string> FilesContaining(string marker)
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

    private static Dictionary<string, SkillEffect> LoadSkillEffects()
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

    private static HashSet<string> LoadCapturingRecipeSkills()
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

    private static Dictionary<string, List<string>> LoadSkillNamesByTypeName()
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
