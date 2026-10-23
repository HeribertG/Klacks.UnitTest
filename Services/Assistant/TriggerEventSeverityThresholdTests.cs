// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the severity thresholds of the proactive trigger events: where each event flips from Low to
/// Medium to High. The values are business rules (how urgent a planner is told something is), so a
/// refactoring that names them must keep every boundary exactly where it was.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TriggerEventSeverityThresholdTests
{
    private static readonly DateOnly SomeDay = new(2026, 10, 1);

    [TestCase(-5, AgentTriggerSeverity.High)]
    [TestCase(0, AgentTriggerSeverity.High)]
    [TestCase(7, AgentTriggerSeverity.High)]
    [TestCase(8, AgentTriggerSeverity.Medium)]
    [TestCase(30, AgentTriggerSeverity.Medium)]
    [TestCase(31, AgentTriggerSeverity.Low)]
    public void UncutFullDayShift_SeverityFollowsDaysUntil(int daysUntil, string expected)
    {
        var trigger = new UncutFullDayShiftTriggerEvent(Guid.NewGuid(), SomeDay, daysUntil, []);

        trigger.Severity.ShouldBe(expected);
    }

    [TestCase(0, AgentTriggerSeverity.High)]
    [TestCase(7, AgentTriggerSeverity.High)]
    [TestCase(8, AgentTriggerSeverity.Medium)]
    [TestCase(30, AgentTriggerSeverity.Medium)]
    [TestCase(31, AgentTriggerSeverity.Low)]
    public void ContractExpiringSoon_SeverityFollowsDaysUntilExpiry(int daysUntilExpiry, string expected)
    {
        var trigger = new ContractExpiringSoonTriggerEvent(Guid.NewGuid(), Guid.NewGuid(), "Ann", SomeDay, daysUntilExpiry);

        trigger.Severity.ShouldBe(expected);
    }

    [TestCase(48, AgentTriggerSeverity.Low)]
    [TestCase(71, AgentTriggerSeverity.Low)]
    [TestCase(72, AgentTriggerSeverity.Medium)]
    [TestCase(167, AgentTriggerSeverity.Medium)]
    [TestCase(168, AgentTriggerSeverity.High)]
    [TestCase(500, AgentTriggerSeverity.High)]
    public void ScenarioPending_SeverityFollowsHoursPending(int hoursPending, string expected)
    {
        var trigger = new ScenarioPendingTriggerEvent(Guid.NewGuid(), hoursPending, null, "North");

        trigger.Severity.ShouldBe(expected);
    }

    [TestCase(-1, AgentTriggerSeverity.High)]
    [TestCase(0, AgentTriggerSeverity.High)]
    [TestCase(1, AgentTriggerSeverity.High)]
    [TestCase(2, AgentTriggerSeverity.Medium)]
    [TestCase(3, AgentTriggerSeverity.Medium)]
    [TestCase(4, AgentTriggerSeverity.Low)]
    public void PeriodCloseDue_SeverityFollowsDaysUntilDue(int daysUntilDue, string expected)
    {
        var trigger = new PeriodCloseDueTriggerEvent(Guid.NewGuid(), "North", SomeDay, daysUntilDue);

        trigger.Severity.ShouldBe(expected);
    }

    [TestCase(0, AgentTriggerSeverity.Medium)]
    [TestCase(1, AgentTriggerSeverity.Medium)]
    [TestCase(2, AgentTriggerSeverity.High)]
    [TestCase(3, AgentTriggerSeverity.High)]
    public void LockConflictDetected_SeverityFollowsLockLevel(int lockLevel, string expected)
    {
        var trigger = new LockConflictDetectedTriggerEvent(Guid.NewGuid(), SomeDay, lockLevel, []);

        trigger.Severity.ShouldBe(expected);
    }
}
