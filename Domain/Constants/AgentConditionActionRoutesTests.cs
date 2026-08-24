// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins that every currently ledger-tracked TriggerKind (PlannersOnly or AdminOnly, per
/// AgentConditionLedgerPolicy.IsLedgerTracked - the 12 kinds AgentConditionRepositoryTests /
/// list_open_findings can actually surface) has a non-null AgentConditionActionRoutes entry, so a
/// forgotten mapping fails a test instead of silently returning null to a chat user. NOT exhaustive
/// against every future kind: a brand-new detector needs both a new TestCase here and a new map entry -
/// the same maintenance contract SensitiveSkills/ReadOnlyExtras already carry in SkillRiskClassifier.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class AgentConditionActionRoutesTests
{
    [TestCase(AgentTriggerKinds.UnstaffedShift)]
    [TestCase(AgentTriggerKinds.LockConflict)]
    [TestCase(AgentTriggerKinds.TargetHoursDrift)]
    [TestCase(AgentTriggerKinds.ScenarioPending)]
    [TestCase(AgentTriggerKinds.PeriodCloseDue)]
    [TestCase(AgentTriggerKinds.ContractExpiringSoon)]
    [TestCase(AgentTriggerKinds.OpenOrder)]
    [TestCase(AgentTriggerKinds.UncutFulldayShift)]
    [TestCase(AgentTriggerKinds.EmptyContainer)]
    [TestCase(AgentTriggerKinds.AvailabilityGap)]
    [TestCase(AgentTriggerKinds.PeriodOverdue)]
    [TestCase(AgentTriggerKinds.ClientMissingCoreData)]
    public void For_EveryLedgerTrackedKind_ReturnsANonNullRoute(string kind)
    {
        AgentConditionActionRoutes.For(kind).ShouldNotBeNull();
    }

    [Test]
    public void For_UnknownKind_ReturnsNull()
    {
        AgentConditionActionRoutes.For("some_kind_nobody_registered").ShouldBeNull();
    }
}
