// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Justification guard for destructive skills: every enabled seed skill whose name starts with
/// delete_ or remove_ that the classifier reports as Irreversible needs a written English reason why
/// unconfirmed execution is acceptable. Irreversible is what a skill has to be listed for in
/// SkillRiskClassifier.IrreversibleSkills, and it is the class that runs WITHOUT any confirmation at the
/// default Autonomous autonomy level - so the coverage guard (WriteSkillRiskDecisionCoverageTests) forces
/// the decision to exist, and this map forces it to be argued for the destructive ones. A skill the
/// classifier decides differently (Sensitive, Reversible or ScenarioGated) does not belong here, which
/// the stale check enforces along with skills that disappeared from the seeds.
/// </summary>

using Klacks.Api.Application.Skills.Meta;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class DestructiveSkillRiskDecisionGuardTests
{
    private static readonly string[] DestructiveNamePrefixes = ["delete_", "remove_"];

    /// <summary>
    /// Destructive skills consciously accepted to run as Irreversible WITHOUT confirmation on the
    /// default Autonomous autonomy level. Every entry needs an honest, specific justification.
    /// A delete_/remove_ skill that SkillRiskClassifier puts in IrreversibleSkills but that is not
    /// justified here fails the guard until someone writes the reason down. Entries marked as
    /// ESCALATION CANDIDATE are accepted for now but should be promoted to SensitiveSkills in
    /// SkillRiskClassifier.
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
            ["delete_counter_rule"] =
                "Soft-deletes one event-counter threshold row; the evaluator only raises a warning " +
                "or error notification against real work rows and never rewrites recorded hours or " +
                "wages, and create_counter_rule restores every field identically.",
            ["delete_communication"] =
                "Soft-deletes a single communication entry (email, phone or note) on a client " +
                "addressed by UUID; the client record itself is untouched and the entry is " +
                "re-enterable.",
            ["delete_expense"] =
                "Deletes a single expense row; day-lock rules are enforced and period-hour " +
                "recalculation plus schedule notifications run automatically, so the books stay " +
                "consistent and redoing the entry in the UI is equally fast.",
            ["delete_export_format_override"] =
                "Soft-deletes the patch override for one export format key, which only reverts that " +
                "format to its shipped default; set_export_format_override recreates an equivalent " +
                "row once the caller supplies the patch again, since the delete does not echo the " +
                "removed patch back.",
            ["delete_individual_period"] =
                "Cascades the soft-delete only to the aggregate's own child period rows and is " +
                "blocked while any non-deleted contract still references it; an assigned individual " +
                "period has no effect on hour computation today, so a wrong delete cannot change a " +
                "computed value, and create_individual_period restores name and stretches.",
            ["delete_llm_model"] =
                "Removes one LLM model configuration row; technical admin configuration without " +
                "business or personal data, re-created by the provider model sync or manually.",
            ["delete_llm_provider"] =
                "Removes one LLM provider configuration row; purely technical admin configuration " +
                "with no cascade into business data, restorable by re-entering the configuration.",
            ["delete_period_cap_rule"] =
                "Soft-deletes one statutory hours or overtime cap row; the evaluator only raises a " +
                "warning or error notification against persisted hours and never recomputes them, " +
                "and create_period_cap_rule restores every field identically.",
            ["delete_qualification"] =
                "Soft-deletes one qualification master row after verifying it exists (by id or " +
                "unambiguous name); qualifications drive candidate matching in planning, not " +
                "payroll computation, and the entry is re-creatable.",
            ["delete_report_template"] =
                "Soft-deletes a report template that no other table references; the create skill " +
                "restores name, description, type and the row flags, but a hand-built layout has to " +
                "be rebuilt on the reports page, and no wage, period-close or computed value is " +
                "touched either way.",
            ["delete_restricted_time_window_rule"] =
                "Soft-deletes one seasonal forbidden-hours rule; the evaluator only raises a warning " +
                "or error notification against real work rows and never rewrites recorded hours or " +
                "wages, and create_restricted_time_window_rule restores season bounds, daily window " +
                "and group tag identically.",
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
            ["remove_client_contract"] =
                "Removes the ClientContract row linking one person to a contract template; the " +
                "template itself is never touched and the assignment is re-created identically by " +
                "assign_contract_by_name from the same client and contract names.",
            ["remove_client_qualification"] =
                "Removes the ClientQualification row recording that one person holds a " +
                "qualification; the catalogue entry stays for everyone else and " +
                "set_client_qualification restores the row from the same client, qualification " +
                "and level.",
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

    [Test]
    public void EveryDestructiveSkill_MustHaveAnExplicitRiskDecision()
    {
        var classifier = new SkillRiskClassifier();
        var undecided = new List<string>();

        foreach (var skill in LoadDestructiveSeedSkills().OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var riskClass = classifier.Classify(SkillSeedCatalog.ToDescriptor(skill));
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
            "Destructive skills (delete_*/remove_*) that SkillRiskClassifier.IrreversibleSkills lets " +
            "run WITHOUT confirmation on the default Autonomous autonomy level need the reason " +
            "written down: EITHER move the skill to a stricter decision in SkillRiskClassifier " +
            "(SensitiveSkills for always-confirm, ReversibleExtras if a true inverse exists, " +
            "ScenarioGatedSkills if mutations land in a scenario) OR add it to " +
            "AcceptedIrreversibleDeletes in this test with an honest justification why unconfirmed " +
            "execution is acceptable. Unjustified skills: " + string.Join(", ", undecided));
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

            var riskClass = classifier.Classify(SkillSeedCatalog.ToDescriptor(skill));
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

    private static IReadOnlyCollection<SeedSkill> LoadDestructiveSeedSkills()
    {
        return SkillSeedCatalog.EnabledSkills()
            .Where(skill => DestructiveNamePrefixes.Any(
                prefix => skill.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
