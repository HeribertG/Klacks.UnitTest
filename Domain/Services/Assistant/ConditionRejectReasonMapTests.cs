// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the direction of the scenario-to-ledger reason translation. The point of the map is that a
/// substantive objection to ONE proposal never reads as "never raise this finding again", so the guard
/// that matters is the one asserting GenerallyUnwanted is unreachable from here, plus exhaustiveness
/// over the RejectReason enum so a new member cannot silently fall through.
/// </summary>

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ConditionRejectReasonMapTests
{
    [TestCase(RejectReason.CoverageDrop)]
    [TestCase(RejectReason.HoursImbalance)]
    [TestCase(RejectReason.PreferenceViolation)]
    [TestCase(RejectReason.QualificationConcern)]
    [TestCase(RejectReason.TooMuchChurn)]
    [TestCase(RejectReason.Other)]
    public void FromScenarioRejection_ForASubstantiveObjection_FaultsTheProposalNotTheFinding(RejectReason reason)
    {
        ConditionRejectReasonMap.FromScenarioRejection(reason)
            .ShouldBe(AgentConditionRejectReason.WrongThisTime);
    }

    [Test]
    public void FromScenarioRejection_ForNoReasonAtAll_RecordsNoReason()
    {
        ConditionRejectReasonMap.FromScenarioRejection(null).ShouldBe(AgentConditionRejectReason.NoReason);
    }

    [Test]
    public void FromScenarioRejection_ForUnspecified_RecordsNoReason()
    {
        ConditionRejectReasonMap.FromScenarioRejection(RejectReason.Unspecified)
            .ShouldBe(AgentConditionRejectReason.NoReason);
    }

    [Test]
    public void FromScenarioRejection_NeverReportsGenerallyUnwanted_BecauseOneRefusalIsNoVerdictOnTheKind()
    {
        Enum.GetValues<RejectReason>()
            .Select(reason => ConditionRejectReasonMap.FromScenarioRejection(reason))
            .ShouldNotContain(AgentConditionRejectReason.GenerallyUnwanted);
    }

    [Test]
    public void FromScenarioRejection_CoversEveryRejectReasonMember()
    {
        Enum.GetValues<RejectReason>()
            .ShouldAllBe(reason => Enum.IsDefined(ConditionRejectReasonMap.FromScenarioRejection(reason)));
    }
}
