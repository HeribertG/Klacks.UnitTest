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
/// be listed with a reason); skills seeded only by plugins; and any fuzzy or substring matching that does not go
/// through NameResolution, which this scan cannot see because it only looks for the Lenient policy. The fuzzy client
/// search has its own guard (ClientFuzzySearchGuardTests); list_groups selects with NameResolution.Partition and is
/// covered by ListGroupsSkillTests only.
/// </summary>

using static Klacks.UnitTest.TestHelpers.SkillGuardSources;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class NameMatchPolicyLenientGuardTests
{
    private const string LenientMarker = "NameMatchPolicy.Lenient";
    private const string FuzzyContractModeMarker = "ContractNameMatchMode.WithFuzzy";
    private const string HolidayTargetResolverMarker = "IHolidayCalendarTargetResolver";
    private const string GroupSearchRepositoryMarker = "IGroupSearchRepository";
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
}
