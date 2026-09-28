// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that every currently ledger-tracked TriggerKind (PlannersOnly or AdminOnly, per
/// AgentConditionLedgerPolicy.IsLedgerTracked - the 12 kinds AgentConditionRepositoryTests /
/// list_open_findings can actually surface) has a non-null AgentConditionActionRoutes entry, so a
/// forgotten mapping fails a test instead of silently returning null to a chat user. NOT exhaustive
/// against every future kind: a brand-new detector needs both a new TestCase here and a new map entry -
/// the same maintenance contract SensitiveSkills/ReadOnlyExtras already carry in SkillRiskClassifier.
/// Also pins the one entity-bound route (empty_container opens the container's own slot-template page)
/// and that it matches the route the live notification carries, so the two copies cannot drift apart.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;

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
    [TestCase(AgentTriggerKinds.UngroupedWorkforce)]
    [TestCase(AgentTriggerKinds.UngroupedShifts)]
    public void For_EveryLedgerTrackedKind_ReturnsANonNullRoute(string kind)
    {
        AgentConditionActionRoutes.For(kind, Guid.NewGuid()).ShouldNotBeNull();
        AgentConditionActionRoutes.For(kind, null).ShouldNotBeNull();
    }

    [Test]
    public void For_UnknownKind_ReturnsNull()
    {
        AgentConditionActionRoutes.For("some_kind_nobody_registered", Guid.NewGuid()).ShouldBeNull();
    }

    [Test]
    public void For_EmptyContainerWithEntity_OpensThatContainersTemplatePage()
    {
        var containerId = Guid.NewGuid();

        AgentConditionActionRoutes.For(AgentTriggerKinds.EmptyContainer, containerId)
            .ShouldBe($"/workplace/container-template/{containerId}");
    }

    [Test]
    public void For_EmptyContainerWithoutEntity_FallsBackToTheShiftList()
    {
        AgentConditionActionRoutes.For(AgentTriggerKinds.EmptyContainer, null)
            .ShouldBe(ProactiveActionRoutes.ShiftList);
    }

    [Test]
    public void EmptyContainerEvent_CarriesTheSameRouteAsTheLedgerMapping_AndNoQueryParams()
    {
        var triggerEvent = new EmptyContainerTriggerEvent(
            Guid.NewGuid(),
            "Tag 135",
            new DateOnly(2025, 1, 1),
            null,
            Array.Empty<Guid>(),
            new ContainerScheduleSnapshot(new TimeOnly(8, 0), new TimeOnly(16, 0), [1, 2, 3], false, false),
            false);

        triggerEvent.ActionRoute.ShouldBe($"/workplace/container-template/{triggerEvent.ShiftId}");
        triggerEvent.ActionRoute.ShouldBe(AgentConditionActionRoutes.For(triggerEvent.Kind, triggerEvent.EntityId));
        triggerEvent.ActionParams.ShouldBeNull();
    }
}
