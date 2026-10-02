// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the contract of the collective unstaffed-shift message: kind, severity thresholds over the days until
/// the first gap, group-scoped planner audience without an entity, the state-based dedup key, the schedule
/// deep link and the scalar parameters that keep the inbox sentence current.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UnstaffedShiftSummaryTriggerEventTests
{
    private static readonly Guid GroupId = new("11111111-2222-3333-4444-555555555555");

    private static UnstaffedShiftSummaryTriggerEvent Make(int daysUntilFirstGap = 2) => new(
        GroupId,
        "Bern",
        new DateOnly(2026, 10, 1),
        new DateOnly(2026, 10, 31),
        12,
        5,
        new DateOnly(2026, 10, 4),
        daysUntilFirstGap);

    [TestCase(0, AgentTriggerSeverity.High)]
    [TestCase(3, AgentTriggerSeverity.High)]
    [TestCase(4, AgentTriggerSeverity.Medium)]
    [TestCase(7, AgentTriggerSeverity.Medium)]
    [TestCase(8, AgentTriggerSeverity.Low)]
    public void Severity_FollowsTheDaysUntilTheFirstGap(int daysUntilFirstGap, string expected)
    {
        Make(daysUntilFirstGap).Severity.ShouldBe(expected);
    }

    [Test]
    public void KeepsTheUnstaffedShiftKind_SoOldPerShiftRowsResolveThroughReconcile()
    {
        Make().Kind.ShouldBe(AgentTriggerKinds.UnstaffedShift);
    }

    [Test]
    public void IsAGroupScopedPlannerMessage_WithoutAnEntity()
    {
        IAgentTriggerEvent triggerEvent = Make();

        triggerEvent.PlannersOnly.ShouldBeTrue();
        triggerEvent.RequiresGroupScope.ShouldBeTrue();
        triggerEvent.GroupId.ShouldBe(GroupId);
        triggerEvent.GroupIds.ShouldBe(new[] { GroupId });
        triggerEvent.EntityId.ShouldBeNull();
    }

    [Test]
    public void DedupKey_IsTheGroupPlusThePeriodStart_IndependentOfTheCounts()
    {
        var first = Make();
        var later = first with { GapCount = 3, DayCount = 1, FirstGapDay = new DateOnly(2026, 10, 20), DaysUntilFirstGap = 9 };

        first.DedupKey.ShouldBe($"{GroupId}:2026-10-01");
        later.DedupKey.ShouldBe(first.DedupKey);
        UnstaffedShiftSummaryTriggerEvent.DedupKeyFor(GroupId, new DateOnly(2026, 10, 1)).ShouldBe(first.DedupKey);
    }

    [Test]
    public void Summary_IsTheSummaryKeyWithScalarParameters()
    {
        var triggerEvent = Make();

        triggerEvent.Summary.ShouldBe(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.UnstaffedShiftSummary);
        triggerEvent.SummaryParams.ShouldBe(new Dictionary<string, string>
        {
            ["group"] = "Bern",
            ["count"] = "12",
            ["days"] = "5",
            ["from"] = "04.10.2026",
            ["until"] = "31.10.2026"
        });
    }

    [Test]
    public void ActionLink_OpensTheScheduleOfTheGroupOnTheFirstGapDay()
    {
        var triggerEvent = Make();

        triggerEvent.ActionRoute.ShouldBe(ProactiveActionRoutes.Schedule);
        triggerEvent.ActionParams.ShouldNotBeNull();
        triggerEvent.ActionParams![ProactiveActionParamKeys.GroupId].ShouldBe(GroupId.ToString());
        triggerEvent.ActionParams[ProactiveActionParamKeys.Date].ShouldBe("2026-10-04");
    }

    [Test]
    public void Payload_RepeatsEverySummaryParameterAsAScalar_SoTheInboxSentenceStaysCurrent()
    {
        var triggerEvent = Make();

        foreach (var (name, value) in triggerEvent.SummaryParams)
        {
            triggerEvent.Payload[name].ShouldBe(value);
        }
    }
}
