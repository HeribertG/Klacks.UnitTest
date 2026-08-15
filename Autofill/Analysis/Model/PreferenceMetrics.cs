// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Autofill.Analysis.Model;

/// <summary>
/// What the plan made of the scenario's master-data shift preferences. Empty for a scenario that
/// declares none.
/// <para>
/// The two halves are not symmetric, and the metric keeps them apart on purpose. The BLACKLIST half is
/// a rule the engine states as a hard stage-0 veto, so a non-empty list is a defect. The PREFERRED
/// half is a soft term, so the satisfaction figures are reported and never asserted as a floor per
/// assignment; the only hard question asked of them is whether granting one broke something harder.
/// </para>
/// </summary>
/// <param name="BlacklistViolations">Assignments that stand on a blacklisted shift; must be empty</param>
/// <param name="FalsePositiveViolations">Preferred assignments that take part in a hard finding; must be empty</param>
/// <param name="SatisfactionByEmployee">Per-employee share of preferred assignments, in list order</param>
public sealed record PreferenceMetrics(
    IReadOnlyList<PreferenceBlacklistViolation> BlacklistViolations,
    IReadOnlyList<PreferenceFalsePositive> FalsePositiveViolations,
    IReadOnlyList<PreferenceSatisfaction> SatisfactionByEmployee)
{
    /// <summary>The measurement of a scenario without preferences.</summary>
    public static PreferenceMetrics Empty { get; } = new([], [], []);
}
