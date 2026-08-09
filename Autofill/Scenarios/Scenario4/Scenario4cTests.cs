// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Run S4c — the symmetry run. The first and the third order exchange their pinned id triples and the
/// shifts are appended back to front, while the carry-in stays bound to its labels. Everything the
/// specification describes is unchanged; only the names of the things are.
/// <para>
/// The run therefore executes S4a a second time and compares the two measurements label-invariantly.
/// One caveat belongs in every reading of the result: because the three orders are cut identically,
/// their ids are the auction's only sort key, so exchanging the ids also exchanges which order is
/// auctioned first. A difference found here is a statement about the algorithm's sensitivity to the
/// slot order and not automatically a defect.
/// </para>
/// </summary>
[TestFixture]
[Explicit("Scenario 4 run; runs the main scenario twice. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4cTests : Scenario4RunTestBase
{
    private const string ReferenceArtifactName = Scenario4SpecConstants.RunAArtifactName + ".symmetry-reference";

    private DeterministicRunResult? _reference;

    [OneTimeSetUp]
    public void BuildAndRunTheSymmetryPair()
    {
        BuildGuardAndRun(Scenario4CarryInFixture.BuildSymmetryRun, Scenario4SpecConstants.RunCArtifactName);

        var referenceDefinition = Scenario4CarryInFixture.BuildMainRun();
        var referenceProblems = Scenario4FixtureGuard.Validate(referenceDefinition);
        if (referenceProblems.Count > 0)
        {
            RecordFixtureProblems(referenceProblems);
            return;
        }

        _reference = DeterministicRunner.Run(
            referenceDefinition,
            Scenario4SpecConstants.ScenarioName,
            ReferenceArtifactName,
            Scenario4GaParameters.MeasuredReferenceRunMs);
        WriteDiagnosis(ReferenceArtifactName, referenceDefinition, _reference);
    }

    [Test]
    public void S4_13_TheSwappedOrdersMeasureTheSame()
    {
        _reference.ShouldNotBeNull("the symmetry reference run was not executed");
        var differences = Scenario4SymmetryComparison.Compare(_reference!.Metrics, Metrics);

        TestContext.Out.WriteLine(
            $"symmetry.metricsIdenticalToS4a = {(differences.Count == 0).ToString().ToLowerInvariant()}");
        TestContext.Out.WriteLine(
            "symmetry.differingMetrics = " + Scenario4Diagnostics.DescribeMetricDifferences(differences));

        differences.ShouldBeEmpty(
            "S4-13: exchanging the id triples of the first and the third order and reversing the insertion order "
            + "describes the same problem under different names, so every measurement must be identical up to the "
            + "order labels — per-order values are compared as multisets, everything else value for value. Caveat for "
            + "the reading: the ids are also the auction's sort key, so this is a symmetry of the SPECIFICATION and "
            + "the engine is free to be sensitive to it; a difference is a finding about that sensitivity, not "
            + $"automatically a defect. Differing: {Scenario4Diagnostics.DescribeMetricDifferences(differences)}");
    }
}
