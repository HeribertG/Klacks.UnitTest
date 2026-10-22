// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OpenOrdersSummaryTriggerEvent: the per-recipient narrowing (orders of visible groups, orders
/// without any group only with includesUngrouped, null when nothing is left), the counts and the severity derived
/// from the narrowed set (High only for a recipient's own variant - the unnarrowed event the ledger stores is capped
/// at Medium), the rising count, the DedupKey and Kind every variant shares, and the payload that carries no count, so the
/// live payload merge of the inbox and the reminder sweep can never overwrite a planner's narrowed numbers with the
/// installation-wide ones.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class OpenOrdersSummaryTriggerEventTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);
    private static readonly Guid GroupA = Guid.NewGuid();
    private static readonly Guid GroupB = Guid.NewGuid();

    private static OpenOrderSnapshot Order(int daysFromToday, params Guid[] groupIds) =>
        new(Guid.NewGuid(), Today.AddDays(daysFromToday), groupIds);

    [Test]
    public void NarrowTo_KeepsTheOrdersOfVisibleGroups_AndLeavesUngroupedOnesToAdmins()
    {
        var inA = Order(2, GroupA);
        var inAandB = Order(10, GroupA, GroupB);
        var inB = Order(3, GroupB);
        var ungrouped = Order(1);
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [inA, inAandB, inB, ungrouped]);

        var planner = (OpenOrdersSummaryTriggerEvent)summary.NarrowTo(new HashSet<Guid> { GroupA }, includesUngrouped: false)!;
        var admin = (OpenOrdersSummaryTriggerEvent)summary.NarrowTo(new HashSet<Guid> { GroupA, GroupB }, includesUngrouped: true)!;

        Assert.That(planner.Orders, Is.EquivalentTo(new[] { inA, inAandB }));
        Assert.That(planner.Count, Is.EqualTo(2));
        Assert.That(planner.SoonCount, Is.EqualTo(1));
        Assert.That(admin.Count, Is.EqualTo(4));
        Assert.That(admin.SoonCount, Is.EqualTo(3));
    }

    [Test]
    public void NarrowTo_NothingVisible_ReturnsNull()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(2, GroupA), Order(3)]);

        Assert.That(summary.NarrowTo(new HashSet<Guid> { GroupB }, includesUngrouped: false), Is.Null);
    }

    [Test]
    public void NarrowTo_OnlyUngroupedOrders_ReachOnlyARecipientWhoSeesUngrouped()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(2), Order(20)]);

        Assert.That(summary.NarrowTo(new HashSet<Guid>(), includesUngrouped: false), Is.Null);
        Assert.That(((OpenOrdersSummaryTriggerEvent)summary.NarrowTo(new HashSet<Guid>(), includesUngrouped: true)!).Count, Is.EqualTo(2));
    }

    [Test]
    public void NarrowTo_KeepsKindSummaryKeyAndDedupKey_SoEveryVariantReportsTheSameCondition()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(2, GroupA), Order(3, GroupB)]);

        var narrowed = summary.NarrowTo(new HashSet<Guid> { GroupA }, includesUngrouped: false)!;

        Assert.That(narrowed.Kind, Is.EqualTo(summary.Kind));
        Assert.That(narrowed.Summary, Is.EqualTo(summary.Summary));
        Assert.That(narrowed.DedupKey, Is.EqualTo(summary.DedupKey));
        Assert.That(AgentConditionLedgerPolicy.FingerprintFor(narrowed), Is.EqualTo(AgentConditionLedgerPolicy.FingerprintFor(summary)));
    }

    [Test]
    public void Severity_OfARecipientsVariant_IsHighWhenAnOrderStartsWithinSevenDays_MediumOtherwise()
    {
        var soon = new OpenOrdersSummaryTriggerEvent(Today, [Order(OpenOrdersSummaryTriggerEvent.SoonWindowDays), Order(40)], IsRecipientView: true);
        var later = new OpenOrdersSummaryTriggerEvent(Today, [Order(OpenOrdersSummaryTriggerEvent.SoonWindowDays + 1), Order(-2)], IsRecipientView: true);

        Assert.That(soon.Severity, Is.EqualTo(AgentTriggerSeverity.High));
        Assert.That(later.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
        Assert.That(later.SoonCount, Is.Zero);
    }

    [Test]
    public void Severity_FollowsTheNarrowedSet_NotTheInstallationWideOne()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(1, GroupA), Order(30, GroupB)]);

        var plannerOfA = summary.NarrowTo(new HashSet<Guid> { GroupA }, includesUngrouped: false)!;
        var plannerOfB = summary.NarrowTo(new HashSet<Guid> { GroupB }, includesUngrouped: false)!;

        Assert.That(plannerOfA.Severity, Is.EqualTo(AgentTriggerSeverity.High));
        Assert.That(plannerOfB.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public void Severity_OfTheUnnarrowedEventTheLedgerStores_IsNeverHigh()
    {
        // The ledger row is shared by every planner who may see any of its groups, and the daily digest counts its
        // severity for each of them; a High there would let group A's orders wake the planner of group B at night.
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(0, GroupA), Order(1, GroupB), Order(2)]);

        Assert.That(summary.SoonCount, Is.EqualTo(3));
        Assert.That(summary.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public void RisingCount_IsTheVariantsCount()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(2, GroupA), Order(3, GroupB), Order(4)]);

        var narrowed = (IRisingCountTriggerEvent)summary.NarrowTo(new HashSet<Guid> { GroupA }, includesUngrouped: false)!;

        Assert.That(((IRisingCountTriggerEvent)summary).RisingCount, Is.EqualTo(3));
        Assert.That(narrowed.RisingCount, Is.EqualTo(1));
    }

    [Test]
    public void Message_IsTheI18nSummaryWithCountAndSoonCount_ForPlannersOnlyAndGroupScoped()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(1, GroupA), Order(30)]);

        Assert.That(summary.Summary, Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.OpenOrdersSummary));
        Assert.That(summary.SummaryParams, Is.EqualTo(new Dictionary<string, string> { ["count"] = "2", ["soonCount"] = "1" }));
        Assert.That(summary.PlannersOnly, Is.True);
        Assert.That(summary.RequiresGroupScope, Is.True);
        Assert.That(((IAgentTriggerEvent)summary).TargetUserId, Is.Null);
        Assert.That(AgentConditionLedgerPolicy.IsLedgerTracked(summary), Is.True);
    }

    [Test]
    public void Payload_CarriesNoCount_SoTheLiveMergeCannotLeakInstallationWideNumbers()
    {
        var summary = new OpenOrdersSummaryTriggerEvent(Today, [Order(1, GroupA), Order(2, GroupB)]);

        Assert.That(summary.Payload.Keys, Has.None.AnyOf("count", "soonCount"));
        Assert.That(summary.Payload.Keys, Is.EquivalentTo(new[] { "companyDay" }));
    }

    [Test]
    public void ActionParams_PointAtTheFirstUpcomingOrderDay_OrTodayWhenAllAlreadyStarted()
    {
        var upcoming = new OpenOrdersSummaryTriggerEvent(Today, [Order(-3), Order(9), Order(4)]);
        var allStarted = new OpenOrdersSummaryTriggerEvent(Today, [Order(-3)]);

        Assert.That(upcoming.ActionParams![ProactiveActionParamKeys.Date], Is.EqualTo("2026-10-26"));
        Assert.That(allStarted.ActionParams![ProactiveActionParamKeys.Date], Is.EqualTo("2026-10-22"));
        Assert.That(upcoming.ActionRoute, Is.EqualTo(ProactiveActionRoutes.Schedule));
    }
}
