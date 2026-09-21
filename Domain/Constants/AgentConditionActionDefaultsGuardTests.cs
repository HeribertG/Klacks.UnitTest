// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the two windows of the action dispatcher against the heartbeat they are measured by. An approval
/// is given between ticks and acted on by a LATER tick, so its execution window must span at least two
/// scan intervals - with a window shorter than one interval about half of all approvals would have been
/// withdrawn before any tick could reach them (blocker found 2026-09-21). A stale CLAIM, on the other
/// hand, must expire within one interval, or a crashed claim would survive every following tick.
/// </summary>

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class AgentConditionActionDefaultsGuardTests
{
    private const int MinimumApprovalWindowScanIntervals = 2;

    [Test]
    public void ApprovalExecutionWindow_SpansAtLeastTwoScanIntervals()
    {
        Assert.That(
            AgentConditionActionDefaults.ApprovalExecutionWindowMinutes,
            Is.GreaterThanOrEqualTo(MinimumApprovalWindowScanIntervals * ProactiveHeartbeat.ScanIntervalMinutes),
            "An approval is picked up by a later tick and may be passed over once; a shorter window withdraws approvals nobody could act on.");
    }

    [Test]
    public void StaleClaimWindow_StaysBelowOneScanInterval()
    {
        Assert.That(
            AgentConditionActionDefaults.StaleClaimMinutes,
            Is.LessThan(ProactiveHeartbeat.ScanIntervalMinutes),
            "A crashed claim must be reclaimable by the very next tick.");
    }

    [Test]
    public void ApprovalExecutionWindow_IsWiderThanTheStaleClaimWindow()
    {
        Assert.That(
            AgentConditionActionDefaults.ApprovalExecutionWindowMinutes,
            Is.GreaterThan(AgentConditionActionDefaults.StaleClaimMinutes),
            "The two windows measure different things; folding them into one is the bug this guard exists for.");
    }
}
