// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the events of the autonomous period close and for the lag-aware wording of the
/// period_close_due reminder: dedup keys separate groups, periods and causes; every cause maps to a known
/// i18n key; both events are ledger-tracked under period_auto_close and open the period-closing page; and the
/// reminder keeps its exact pre-lag message without a lag while naming period end and close date apart with one.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodAutoCloseTriggerEventTests
{
    private static readonly Guid GroupId = Guid.Parse("7a1d0000-0000-0000-0000-00000000000a");
    private static readonly DateOnly Start = new(2026, 8, 1);
    private static readonly DateOnly End = new(2026, 8, 31);

    private static PeriodAutoCloseBlockedTriggerEvent Blocked(PeriodAutoCloseBlockReason reason, DateOnly? end = null) =>
        new(GroupId, "Bern", Start, end ?? End, 0, reason);

    [Test]
    public void BlockedEvents_DifferentCauses_HaveDifferentDedupKeys()
    {
        var keys = Enum.GetValues<PeriodAutoCloseBlockReason>().Select(reason => Blocked(reason).DedupKey).ToList();

        Assert.That(keys, Is.Unique);
    }

    [Test]
    public void BlockedEvent_SameCauseAndPeriod_KeepsItsDedupKeyAcrossScans()
    {
        Assert.That(
            (Blocked(PeriodAutoCloseBlockReason.OpenErrors) with { ErrorCount = 7 }).DedupKey,
            Is.EqualTo(Blocked(PeriodAutoCloseBlockReason.OpenErrors).DedupKey));
        Assert.That(
            Blocked(PeriodAutoCloseBlockReason.OpenErrors).DedupKey,
            Is.Not.EqualTo(Blocked(PeriodAutoCloseBlockReason.OpenErrors, new DateOnly(2026, 9, 30)).DedupKey));
    }

    [Test]
    public void BlockedEvent_EveryCauseHasAnI18nKey()
    {
        foreach (var reason in Enum.GetValues<PeriodAutoCloseBlockReason>())
        {
            var key = PeriodAutoCloseBlockedTriggerEvent.I18nKeyFor(reason);

            Assert.That(key, Does.StartWith("assistant.proactive.periodAutoCloseBlocked"), reason.ToString());
            Assert.That(Blocked(reason).Summary, Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + key));
        }
    }

    [Test]
    public void Events_AreLedgerTrackedUnderTheDedicatedKindAndOpenThePeriodClosingPage()
    {
        IAgentTriggerEvent[] events =
        [
            Blocked(PeriodAutoCloseBlockReason.NoLagStored),
            new PeriodAutoClosedTriggerEvent(GroupId, "Bern", Start, End, 1, Guid.NewGuid())
        ];

        foreach (var triggerEvent in events)
        {
            Assert.Multiple(() =>
            {
                Assert.That(triggerEvent.Kind, Is.EqualTo(AgentTriggerKinds.PeriodAutoClose));
                Assert.That(AgentConditionLedgerPolicy.IsLedgerTracked(triggerEvent), Is.True);
                Assert.That(triggerEvent.ActionRoute, Is.EqualTo(ProactiveActionRoutes.PeriodClosing));
                Assert.That(triggerEvent.GroupId, Is.EqualTo(GroupId));
            });
        }

        Assert.That(AgentConditionActionRoutes.For(AgentTriggerKinds.PeriodAutoClose), Is.EqualTo(ProactiveActionRoutes.PeriodClosing));
    }

    [Test]
    public void ClosedEvent_AndBlockedEvents_NeverShareADedupKey()
    {
        var closed = new PeriodAutoClosedTriggerEvent(GroupId, "Bern", Start, End, 1, Guid.NewGuid());

        Assert.That(
            Enum.GetValues<PeriodAutoCloseBlockReason>().Select(reason => Blocked(reason).DedupKey),
            Has.None.EqualTo(closed.DedupKey));
    }

    [Test]
    public void CloseDueReminder_WithoutLag_KeepsThePlainMessageAndItsParameters()
    {
        var reminder = new PeriodCloseDueTriggerEvent(GroupId, "Bern", End, 2);

        Assert.Multiple(() =>
        {
            Assert.That(reminder.Summary, Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.PeriodCloseDue));
            Assert.That(reminder.SummaryParams.Keys, Is.EquivalentTo(new[] { "group", "date", "days" }));
            Assert.That(reminder.SummaryParams["date"], Is.EqualTo("31.08.2026"));
        });
    }

    [Test]
    public void CloseDueReminder_WithLag_NamesPeriodEndAndCloseDateApart()
    {
        var reminder = new PeriodCloseDueTriggerEvent(GroupId, "Bern", End, 1, LagDays: 5);

        Assert.Multiple(() =>
        {
            Assert.That(reminder.Summary, Is.EqualTo(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.PeriodCloseDueWithLag));
            Assert.That(reminder.SummaryParams["periodEnd"], Is.EqualTo("31.08.2026"));
            Assert.That(reminder.SummaryParams["date"], Is.EqualTo("05.09.2026"));
            Assert.That(reminder.SummaryParams["days"], Is.EqualTo("1"));
        });
    }
}
