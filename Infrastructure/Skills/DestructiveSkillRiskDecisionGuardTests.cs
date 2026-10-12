// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Justification guard for destructive skills: every enabled seed skill whose name starts with
/// delete_, remove_ or reset_ that the classifier reports as Irreversible needs a written English reason why
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
    private static readonly string[] DestructiveNamePrefixes = ["delete_", "remove_", "reset_"];

    /// <summary>
    /// Destructive skills consciously accepted to run as Irreversible WITHOUT confirmation on the
    /// default Autonomous autonomy level. Since the owner decision of 2026-10-12 ("only deletes and resets ask")
    /// every delete_/remove_/reset_ skill that destroys business, personal, payroll, compliance or credential data
    /// is Sensitive; what remains here is assistant-internal bookkeeping only. Every entry needs an honest,
    /// specific justification. A delete_/remove_/reset_ skill that SkillRiskClassifier puts in IrreversibleSkills
    /// but that is not justified here fails the guard until someone writes the reason down.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AcceptedIrreversibleDeletes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["delete_agent_skill"] =
                "Disables a user-created skill only (system skills are refused by the implementation) and " +
                "keeps the row; no business data is touched and update_agent_skill can enable it again.",
            ["delete_ai_memory"] =
                "Deletes a single assistant memory row addressed by id; assistant-internal bookkeeping " +
                "without business, personal or payroll data, no cascade, and the memory can be re-added " +
                "via add_ai_memory at any time.",
            ["delete_transcription_dictionary_entry"] =
                "Removes one speech-to-text dictionary term; worst case a term is no longer " +
                "auto-corrected until re-added - no business data involved."
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
            "Destructive skills (delete_*/remove_*/reset_*) that SkillRiskClassifier.IrreversibleSkills lets " +
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
                    $"{acceptedName} (no longer exists as an enabled delete_/remove_/reset_ skill in the seeds)");
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
