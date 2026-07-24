// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Risk-decision guard for destructive skills: every enabled seed skill whose name starts with
/// delete_ or remove_ must carry a conscious risk decision. On the default Autonomous autonomy
/// level only Sensitive skills are confirmed, so a destructive skill that falls through to the
/// Irreversible default in SkillRiskClassifier runs WITHOUT any confirmation. This guard forces
/// a choice per skill: either the classifier classifies it deliberately (Sensitive, Reversible
/// or ScenarioGated via its explicit lists), or the skill is listed in AcceptedIrreversibleDeletes
/// with a written English justification why unconfirmed execution at the default level is
/// acceptable. A stale check keeps the acceptance map honest when skills disappear from the seeds
/// or get reclassified.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Skills.Meta;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class DestructiveSkillRiskDecisionGuardTests
{
    private const string SkillSeedsFileName = "skill-seeds.json";
    private const string SettingsReaderSkillsFileName = "settings-reader-skills.json";
    private const string SkillsJsonProperty = "skills";
    private const string SkillNameJsonProperty = "name";
    private const string SkillCategoryJsonProperty = "category";
    private const string SkillIsEnabledJsonProperty = "isEnabled";

    private static readonly string[] DestructiveNamePrefixes = ["delete_", "remove_"];

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsFeaturesRelativePath =
    [
        "Klacks.Api", "Plugins", "Features"
    ];

    /// <summary>
    /// Destructive skills consciously accepted to run as Irreversible WITHOUT confirmation on the
    /// default Autonomous autonomy level. Every entry needs an honest, specific justification.
    /// A delete_/remove_ skill that is neither classified by SkillRiskClassifier (SensitiveSkills,
    /// ReversibleExtras, ScenarioGatedSkills) nor listed here fails the guard until someone makes
    /// that decision. Entries marked as ESCALATION CANDIDATE are accepted for now but should be
    /// promoted to SensitiveSkills in SkillRiskClassifier.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AcceptedIrreversibleDeletes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["delete_absence"] =
                "Guarded soft delete: the implementation refuses types still used by active breaks " +
                "or break placeholders and honours the Undeletable flag, so booked absences cannot " +
                "be orphaned and the row is re-creatable.",
            ["delete_absence_type"] =
                "Soft-deletes absence master data but refuses protected seeded types and any type " +
                "still used by active bookings or placeholders (blocking counts are reported), so " +
                "no existing absence or payroll-relevant row can be orphaned.",
            ["delete_address"] =
                "Soft-deletes one address row of a client addressed by UUID; the person and all " +
                "other client data remain, the address is re-enterable, and this stays far below " +
                "the delete_client threshold that is Sensitive.",
            ["delete_agent_skill"] =
                "Disables user-created skills only (system skills are refused by the " +
                "implementation); no business data is touched and the definition can be re-created.",
            ["delete_ai_memory"] =
                "Deletes a single assistant memory row addressed by id; no cascade, no payroll " +
                "impact, and the memory can be re-added via add_ai_memory at any time.",
            ["delete_annotation"] =
                "Soft-deletes a single free-text note on a client addressed by UUID; no planning " +
                "or payroll semantics and quickly re-entered.",
            ["delete_break_placeholder"] =
                "Removes one PLANNED absence placeholder and is the registered inverse of " +
                "add_break_placeholder; re-adding restores the exact state, so the practical risk " +
                "matches the Reversible class even though the registry direction defaults it here.",
            ["delete_communication"] =
                "Soft-deletes a single communication entry (email, phone or note) on a client " +
                "addressed by UUID; the client record itself is untouched and the entry is " +
                "re-enterable.",
            ["delete_expense"] =
                "Deletes a single expense row; day-lock rules are enforced and period-hour " +
                "recalculation plus schedule notifications run automatically, so the books stay " +
                "consistent and redoing the entry in the UI is equally fast.",
            ["delete_llm_model"] =
                "Removes one LLM model configuration row; technical admin configuration without " +
                "business or personal data, re-created by the provider model sync or manually.",
            ["delete_llm_provider"] =
                "Removes one LLM provider configuration row; purely technical admin configuration " +
                "with no cascade into business data, restorable by re-entering the configuration.",
            ["delete_qualification"] =
                "Soft-deletes one qualification master row after verifying it exists (by id or " +
                "unambiguous name); qualifications drive candidate matching in planning, not " +
                "payroll computation, and the entry is re-creatable.",
            ["delete_schedule_note"] =
                "Soft-deletes one informational schedule note by UUID and echoes the deleted " +
                "note's client and date back; notes carry no planning or payroll semantics.",
            ["delete_scheduling_rule"] =
                "Deletes one scheduling rule; it only shapes future auto-planning proposals, " +
                "which are scenario-gated before acceptance anyway, and the rule is re-creatable.",
            ["delete_shift"] =
                "Soft-delete that refuses shifts with cuts (a sub-shift tree) or assigned works " +
                "and reports the manual action needed, so the cascading cases that made " +
                "delete_group and delete_branch Sensitive are blocked by the implementation itself.",
            ["delete_transcription_dictionary_entry"] =
                "Removes one speech-to-text dictionary term; worst case a term is no longer " +
                "auto-corrected until re-added — no business data involved.",
            ["delete_workchange"] =
                "Soft-deletes a single WorkChange row (correction, replacement, travel or " +
                "briefing on one Work) and triggers period-hour recalculation automatically; the " +
                "same single-row granularity as the Reversible delete_work.",
            ["remove_client_from_group"] =
                "Removes a single group assignment row with a symmetric inverse " +
                "(add_client_to_group re-creates it from the same two names); unlike the " +
                "Sensitive delete_membership it does not move the plannability boundary.",
            ["remove_container_template_task"] =
                "Removes one task from a container weekday template under the container edit " +
                "lock; affects future template-based planning only and the task can be re-added " +
                "identically.",
            ["remove_shift_from_group"] =
                "Deletes a single group_item link (shift_id + group_id) with the symmetric " +
                "inverse add_shift_to_group; the shift itself and its works stay untouched.",
            ["remove_shift_required_qualification"] =
                "Deletes one (shift, qualification) requirement row previously created via " +
                "set_shift_required_qualification; the symmetric setter restores it exactly."
        };

    private sealed record DestructiveSkill(string Name, SkillCategory Category);

    [Test]
    public void EveryDestructiveSkill_MustHaveAnExplicitRiskDecision()
    {
        var classifier = new SkillRiskClassifier();
        var undecided = new List<string>();

        foreach (var skill in LoadDestructiveSeedSkills().OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var riskClass = classifier.Classify(ToDescriptor(skill));
            if (riskClass != SkillRiskClass.Irreversible)
            {
                continue;
            }

            if (AcceptedIrreversibleDeletes.ContainsKey(skill.Name))
            {
                continue;
            }

            undecided.Add(skill.Name);
        }

        undecided.ShouldBeEmpty(
            "Destructive skills (delete_*/remove_*) classified as Irreversible run WITHOUT " +
            "confirmation on the default Autonomous autonomy level. Each of these skills needs a " +
            "conscious risk decision: EITHER register it in SkillRiskClassifier (SensitiveSkills " +
            "for always-confirm, ReversibleExtras if a true inverse exists, ScenarioGatedSkills if " +
            "mutations land in a scenario) OR add it to AcceptedIrreversibleDeletes in this test " +
            "with an honest justification why unconfirmed execution is acceptable. Undecided " +
            "skills: " + string.Join(", ", undecided));
    }

    [Test]
    public void AcceptedIrreversibleDeletes_MustNotContainStaleEntries()
    {
        var classifier = new SkillRiskClassifier();
        var destructiveByName = LoadDestructiveSeedSkills()
            .ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var staleEntries = new List<string>();

        foreach (var acceptedName in AcceptedIrreversibleDeletes.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!destructiveByName.TryGetValue(acceptedName, out var skill))
            {
                staleEntries.Add(
                    $"{acceptedName} (no longer exists as an enabled delete_/remove_ skill in the seeds)");
                continue;
            }

            var riskClass = classifier.Classify(ToDescriptor(skill));
            if (riskClass != SkillRiskClass.Irreversible)
            {
                staleEntries.Add(
                    $"{acceptedName} (now classified as {riskClass} — the acceptance entry is obsolete)");
            }
        }

        staleEntries.ShouldBeEmpty(
            "AcceptedIrreversibleDeletes must stay an honest inventory of destructive skills that " +
            "really run unconfirmed today. Remove these stale entries: " +
            string.Join("; ", staleEntries));
    }

    [Test]
    public void AcceptedIrreversibleDeletes_MustCarryAJustification()
    {
        var unjustified = AcceptedIrreversibleDeletes
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        unjustified.ShouldBeEmpty(
            "Every accepted irreversible delete needs a written justification — an empty entry is " +
            "not a decision. Affected: " + string.Join(", ", unjustified));
    }

    private static SkillDescriptor ToDescriptor(DestructiveSkill skill)
    {
        return new SkillDescriptor(
            skill.Name,
            string.Empty,
            skill.Category,
            Array.Empty<SkillParameter>(),
            Array.Empty<string>(),
            Array.Empty<LLMCapability>(),
            null);
    }

    private static IReadOnlyCollection<DestructiveSkill> LoadDestructiveSeedSkills()
    {
        var skills = new Dictionary<string, DestructiveSkill>(StringComparer.OrdinalIgnoreCase);
        CollectFromDefinitionsFile(LocateDefinitionsFile(SkillSeedsFileName), skills);
        CollectFromDefinitionsFile(LocateDefinitionsFile(SettingsReaderSkillsFileName), skills);
        CollectFromPluginSeeds(skills);
        return skills.Values;
    }

    private static void CollectFromDefinitionsFile(string filePath, Dictionary<string, DestructiveSkill> skills)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        foreach (var element in document.RootElement.GetProperty(SkillsJsonProperty).EnumerateArray())
        {
            AddIfDestructive(element, skills);
        }
    }

    private static void CollectFromPluginSeeds(Dictionary<string, DestructiveSkill> skills)
    {
        var featuresDir = TryLocateDir(PluginsFeaturesRelativePath);
        if (featuresDir == null)
        {
            return;
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
                AddIfDestructive(element, skills);
            }
        }
    }

    private static void AddIfDestructive(JsonElement element, Dictionary<string, DestructiveSkill> skills)
    {
        if (!element.TryGetProperty(SkillNameJsonProperty, out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var name = nameProperty.GetString()!;
        if (!DestructiveNamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var isEnabled = !element.TryGetProperty(SkillIsEnabledJsonProperty, out var enabled) || enabled.GetBoolean();
        if (!isEnabled)
        {
            return;
        }

        skills.TryAdd(name, new DestructiveSkill(name, ParseCategory(element)));
    }

    private static SkillCategory ParseCategory(JsonElement element)
    {
        if (element.TryGetProperty(SkillCategoryJsonProperty, out var category) &&
            category.ValueKind == JsonValueKind.String &&
            Enum.TryParse<SkillCategory>(category.GetString(), true, out var parsed))
        {
            return parsed;
        }

        // Missing or unknown category falls back to Crud (a write category), so a seed typo can
        // never make a destructive skill look read-only to the classifier.
        return SkillCategory.Crud;
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
