// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the one place where Klacks decides "read-only" twice and could drift. SkillRiskClassifier is
/// category-first: a write category (Crud/Action) beats a read-only name prefix, so a write skill cannot
/// buy itself an un-gated run by being called check_something. Two other consumers are prefix-only -
/// RepeatedWriteCallGuard, which lets a read-only call repeat inside one turn, and
/// AssistantLastActionCall.IsReadOnly, which the correction path's undo offer reads. Every skill whose
/// seeded category is a write category while its name carries a read-only prefix is a skill those two
/// read as harmless and the classifier does not.
///
/// The divergence is allowed only where somebody wrote down why: either the skill is in
/// SkillRiskClassifier.ReadOnlyExtras (the classifier agrees it only reads, so there is no disagreement
/// left) or it is in AcceptedPrefixDivergences below, with its reason. Anything else fails here rather
/// than in production. The allowlist is meant to shrink: a new entry needs a deliberate edit and a
/// justification, which is the whole point of pinning it instead of computing it.
/// </summary>

using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class ReadOnlyPrefixWriteCategoryGuardTests
{
    /// <summary>
    /// Skills that keep a read-only name prefix on a write category on purpose. The skill reads a folder
    /// on the configured ERP drop point and reports whether it is reachable and writable; the seed gives
    /// it category Action because it touches an external system, not because it changes Klacks data. It
    /// is left out of ReadOnlyExtras deliberately: the classifier should keep gating an outbound call,
    /// while the in-turn repeat rule and the undo offer may treat it as the read it is.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AcceptedPrefixDivergences =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["check_erp_drop_point_folder_health"] =
                "reads an external drop point and reports its health; Action only because it leaves the installation"
        };

    [Test]
    public void NoWriteCategorySkill_MayCarryAReadOnlyNamePrefix_UnlessItIsWrittenDown()
    {
        var undeclared = SkillSeedCatalog.EnabledSkills()
            .Where(skill => SkillSeedCatalog.IsWriteCategory(skill.Category))
            .Where(skill => ReadOnlySkillPrefixes.HasReadOnlyPrefix(skill.Name))
            .Where(skill => !SkillRiskClassifier.ReadOnlyExtras.Contains(skill.Name))
            .Where(skill => !AcceptedPrefixDivergences.ContainsKey(skill.Name))
            .Select(skill => $"{skill.Name} ({skill.Category})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        undeclared.ShouldBeEmpty(
            "These skills are seeded in a write category but named with a read-only prefix, so " +
            "SkillRiskClassifier treats them as writes while RepeatedWriteCallGuard and " +
            "AssistantLastActionCall.IsReadOnly treat them as reads. Decide which is true: rename the " +
            "skill, change its seeded category, add it to SkillRiskClassifier.ReadOnlyExtras if it really " +
            "only reads, or add it to AcceptedPrefixDivergences with the reason. Undeclared: " +
            string.Join(", ", undeclared));
    }

    [Test]
    public void TheAllowlist_MustNotNameSkillsThatNoLongerDiverge()
    {
        var diverging = SkillSeedCatalog.EnabledSkills()
            .Where(skill => SkillSeedCatalog.IsWriteCategory(skill.Category))
            .Where(skill => ReadOnlySkillPrefixes.HasReadOnlyPrefix(skill.Name))
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dead = AcceptedPrefixDivergences.Keys
            .Where(name => !diverging.Contains(name) || SkillRiskClassifier.ReadOnlyExtras.Contains(name))
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        dead.ShouldBeEmpty(
            "An accepted divergence for a skill that no longer diverges - renamed, recategorised, gone, " +
            "or since added to ReadOnlyExtras - is dead weight that lets the list grow instead of " +
            "shrink. Remove: " + string.Join(", ", dead));
    }
}
