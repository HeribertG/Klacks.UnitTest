// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Coverage guard for the fail-closed default in SkillRiskClassifier. Since the fall-through returns
/// Sensitive instead of Irreversible, a write skill nobody classified is safe - it is held for
/// confirmation at every autonomy level and refused on every unattended path - but that is a silent
/// surprise nobody decided. This guard names such a skill at build time: no seeded skill may reach the
/// fall-through, every one has to be classified by an explicit collection or by the InverseSkillRegistry.
/// It also keeps the collections honest: no skill decided twice, no entry for a skill the seeds no longer
/// contain, and no Irreversible or ReadOnly entry that an earlier check in Classify() would swallow.
/// A skill may be in one collection AND in the InverseSkillRegistry - Sensitive and ScenarioGated
/// deliberately outrank a registered inverse (close_period has one and is still Sensitive) - so the
/// duplicate check spans the collections only, and the shadowing check covers the two classes an inverse
/// really would swallow.
/// The per-skill justification for the destructive entries lives in DestructiveSkillRiskDecisionGuardTests.
/// </summary>

using Klacks.Api.Application.Skills.Meta;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class WriteSkillRiskDecisionCoverageTests
{
    private const string SensitiveListName = "SensitiveSkills";
    private const string ScenarioGatedListName = "ScenarioGatedSkills";
    private const string ReversibleExtrasListName = "ReversibleExtras";
    private const string ReadOnlyExtrasListName = "ReadOnlyExtras";
    private const string IrreversibleListName = "IrreversibleSkills";
    private const string InverseRegistryName = "InverseSkillRegistry";
    private const string ManualInverseMarker = "__manual__";

    private static readonly IReadOnlyList<(string Name, HashSet<string> Skills)> DecisionLists =
    [
        (SensitiveListName, SkillRiskClassifier.SensitiveSkills),
        (ScenarioGatedListName, SkillRiskClassifier.ScenarioGatedSkills),
        (ReversibleExtrasListName, SkillRiskClassifier.ReversibleExtras),
        (ReadOnlyExtrasListName, SkillRiskClassifier.ReadOnlyExtras),
        (IrreversibleListName, SkillRiskClassifier.IrreversibleSkills)
    ];

    [Test]
    public void NoSeededSkill_MayReachTheSensitiveFallThrough()
    {
        var classifier = new SkillRiskClassifier();

        var undecided = SkillSeedCatalog.EnabledSkills()
            .Where(skill => classifier.Classify(SkillSeedCatalog.ToDescriptor(skill)) == SkillRiskClass.Sensitive)
            .Where(skill => !SkillRiskClassifier.SensitiveSkills.Contains(skill.Name))
            .Select(skill => $"{skill.Name} ({skill.Category})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        undecided.ShouldBeEmpty(
            "These skills classify as Sensitive only because they fall through every check in " +
            "SkillRiskClassifier.Classify - nobody decided their risk. Being held for confirmation at " +
            "every autonomy level is the safe outcome, not the intended one. Put each of them into " +
            $"{SensitiveListName} (always confirm), {ScenarioGatedListName} (mutations land in a scenario " +
            $"a human accepts), {ReversibleExtrasListName} or {InverseRegistryName} (a real inverse " +
            $"restores the state), {ReadOnlyExtrasListName} (it does not actually mutate) or " +
            $"{IrreversibleListName} (an ordinary write that may run unconfirmed at the default autonomy " +
            "level). Undecided skills: " + string.Join(", ", undecided));
    }

    [Test]
    public void NoSkill_MayCarryMoreThanOneRiskDecision()
    {
        var conflicts = DecisionLists
            .SelectMany(list => list.Skills.Select(skill => (Skill: skill, List: list.Name)))
            .GroupBy(entry => entry.Skill, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(" + ", group.Select(entry => entry.List))})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        conflicts.ShouldBeEmpty(
            "A skill listed in two decision collections is decided twice, and only the earlier check in " +
            "Classify() ever takes effect - the second entry reads as a decision but is dead. Keep one " +
            "and delete the other: " + string.Join("; ", conflicts));
    }

    [Test]
    public void RiskDecisionLists_MustNotNameSkillsThatTheSeedsNoLongerContain()
    {
        var seededNames = SkillSeedCatalog.EnabledSkills()
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dead = DecisionLists
            .SelectMany(list => list.Skills.Select(skill => (Skill: skill, List: list.Name)))
            .Where(entry => !seededNames.Contains(entry.Skill))
            .Select(entry => $"{entry.Skill} (in {entry.List})")
            .Concat(InverseSkillRegistry.Map.Keys
                .Where(name => !seededNames.Contains(name))
                .Select(name => $"{name} (in {InverseRegistryName})"))
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        dead.ShouldBeEmpty(
            "A decision for a skill that no longer exists is dead weight, and it hides that nobody " +
            "reviewed the list when the skill went away - a renamed skill silently loses its " +
            "classification this way. Remove: " + string.Join("; ", dead));
    }

    [Test]
    public void IrreversibleAndReadOnlyEntries_MustNotBeShadowedByARegisteredInverse()
    {
        var shadowed = new[] { IrreversibleListName, ReadOnlyExtrasListName }
            .SelectMany(listName => DecisionLists.Single(list => list.Name == listName).Skills
                .Where(HasEffectiveInverse)
                .Select(skill => $"{skill} (in {listName})"))
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        shadowed.ShouldBeEmpty(
            "Classify() checks the inverse registry BEFORE these two collections, so such an entry never " +
            $"takes effect - the skill classifies as Reversible. Either drop the {InverseRegistryName} " +
            "mapping or drop the entry: " + string.Join("; ", shadowed));
    }

    private static bool HasEffectiveInverse(string skillName)
    {
        return InverseSkillRegistry.TryGet(skillName, out var inverse)
               && !string.Equals(inverse.SkillName, ManualInverseMarker, StringComparison.Ordinal);
    }
}
