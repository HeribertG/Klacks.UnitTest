// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;
using NUnit.Framework;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario1;

/// <summary>
/// Scenario 1 of the specification, the clean start: March 2026, an empty previous month, three
/// shifts a day (07-15 / 15-23 / 23-07) and five employees at 180 guaranteed hours in list rank 1 to
/// 5. Demand is 93 shifts (744 h) against 900 h of offered time, so 19.5 shifts are structurally
/// missing and rule 6 predicts ranks 1 to 4 near 176 h and rank 5 near 40 h. The assertions A1 to A9
/// live in <see cref="Scenario1AssertionsBase"/>; this fixture only pins the scenario's numbers.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Scenario1Tests : Scenario1AssertionsBase
{
    private const string ArtifactScenarioName = "scenario1";

    private const string ArtifactFileName = "Scenario1CleanStart";

    protected override string ScenarioName => ArtifactScenarioName;

    protected override string ArtifactTestName => ArtifactFileName;

    protected override double GuaranteedHours => AutofillSpecConstants.GuaranteedHours;

    /// <summary>
    /// A6 measures the 95 % threshold of scenario 1 against the reference of 176 h (22 shifts in
    /// clean 5/2 packages), not against the 180 guaranteed hours: 180 h cannot be reached by anyone
    /// while the period only holds 93 shifts.
    /// </summary>
    protected override double ReferenceHours => AutofillSpecConstants.ReferenceHoursTopRanks;

    /// <summary>Rank 5 is exempt from A6 and A8 because the top-down rule leaves it near 40 h.</summary>
    protected override int HighestAssertedRank => AutofillSpecConstants.TopRankUpperBound;
}
