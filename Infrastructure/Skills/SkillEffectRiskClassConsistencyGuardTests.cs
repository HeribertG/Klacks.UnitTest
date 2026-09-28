// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Ties the curated skill effect to SkillRiskClassifier for the one consumer that uses the effect as a safety
/// signal: ReadOnlyRecipeWriteGuard lets a call run after a completed recipe when its skill's effect is Explain,
/// Read or Advise. That is only safe while no such skill is one the classifier would gate, so every enabled seed
/// skill (core catalogue, settings readers and feature plugins) whose effect is not Mutate must classify ReadOnly.
/// The other direction is deliberately not required: the classifier lets draft and proposal steps such as
/// create_plan run ungated although they carry Mutate, and the guard rejects exactly those.
/// </summary>

using Klacks.Api.Application.Skills.Meta;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class SkillEffectRiskClassConsistencyGuardTests
{
    [Test]
    public void EverySkillWhoseEffectIsNotMutate_ClassifiesReadOnly()
    {
        var classifier = new SkillRiskClassifier();

        var gated = SkillSeedCatalog.EnabledSkills()
            .Where(skill => skill.Effect != SkillEffect.Mutate)
            .Select(skill => (Skill: skill, RiskClass: classifier.Classify(SkillSeedCatalog.ToDescriptor(skill))))
            .Where(entry => entry.RiskClass != SkillRiskClass.ReadOnly)
            .Select(entry => $"{entry.Skill.Name} (effect {entry.Skill.Effect}, category {entry.Skill.Category}, class {entry.RiskClass})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        gated.ShouldBeEmpty(
            "These skills are not Mutate by their effect, so ReadOnlyRecipeWriteGuard lets them run after a completed " +
            "recipe, but SkillRiskClassifier gates them. Either the effect is wrong (make it Mutate) or the classifier " +
            "is (allow-list the skill as read-only): " + string.Join("; ", gated));
    }

    [Test]
    public void TheSeedCatalogue_ContainsSkillsOfEveryNonMutateEffect()
    {
        var effects = SkillSeedCatalog.EnabledSkills().Select(skill => skill.Effect).ToHashSet();

        effects.ShouldContain(SkillEffect.Explain);
        effects.ShouldContain(SkillEffect.Read);
        effects.ShouldContain(SkillEffect.Advise);
    }
}
