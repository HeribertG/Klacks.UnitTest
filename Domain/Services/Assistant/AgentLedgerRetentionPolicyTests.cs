// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using NUnit.Framework;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AgentLedgerRetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private static bool Eligible(AgentCondition condition) =>
        AgentLedgerRetentionPolicy
            .ConditionEligible(
                AgentLedgerRetentionPolicy.ShortLivedConditionCutoff(Now),
                AgentLedgerRetentionPolicy.LongLivedConditionCutoff(Now))
            .Compile()(condition);

    private static AgentCondition Condition(AgentConditionStatus status, DateTime lastSeenAtUtc) =>
        new() { Status = status, LastSeenAtUtc = lastSeenAtUtc };

    private static DateTime DaysAgo(int days) => Now.AddDays(-days);

    [Test]
    public void Constants_MatchTheDecidedRetentionWindows()
    {
        Assert.That(AgentLedgerRetentionDefaults.ResolvedOrRejectedConditionDays, Is.EqualTo(90));
        Assert.That(AgentLedgerRetentionDefaults.ExecutedOrEscalatedConditionDays, Is.EqualTo(365));
        Assert.That(AgentLedgerRetentionDefaults.DispatchDays, Is.EqualTo(180));
    }

    [Test]
    public void Resolved_IsEligibleOnlyAfter90DaysFromResolvedAt()
    {
        var old = Condition(AgentConditionStatus.Resolved, DaysAgo(400));
        old.ResolvedAtUtc = DaysAgo(91);
        var recent = Condition(AgentConditionStatus.Resolved, DaysAgo(400));
        recent.ResolvedAtUtc = DaysAgo(89);

        Assert.That(Eligible(old), Is.True);
        Assert.That(Eligible(recent), Is.False);
    }

    [Test]
    public void Resolved_WithoutResolvedAt_FallsBackToLastSeen()
    {
        Assert.That(Eligible(Condition(AgentConditionStatus.Resolved, DaysAgo(91))), Is.True);
        Assert.That(Eligible(Condition(AgentConditionStatus.Resolved, DaysAgo(10))), Is.False);
    }

    [Test]
    public void Rejected_UsesHandledAtThenLastSeen()
    {
        var old = Condition(AgentConditionStatus.Rejected, DaysAgo(10));
        old.HandledAtUtc = DaysAgo(120);
        var recent = Condition(AgentConditionStatus.Rejected, DaysAgo(400));
        recent.HandledAtUtc = DaysAgo(5);

        Assert.That(Eligible(old), Is.True);
        Assert.That(Eligible(recent), Is.False);
        Assert.That(Eligible(Condition(AgentConditionStatus.Rejected, DaysAgo(100))), Is.True);
    }

    [Test]
    public void Executed_IsKeptFor365Days()
    {
        var kept = Condition(AgentConditionStatus.Executed, DaysAgo(200));
        kept.HandledAtUtc = DaysAgo(364);
        var expired = Condition(AgentConditionStatus.Executed, DaysAgo(200));
        expired.HandledAtUtc = DaysAgo(366);

        Assert.That(Eligible(kept), Is.False);
        Assert.That(Eligible(expired), Is.True);
    }

    [Test]
    public void Escalated_UsesEscalatedAtThenLastSeenAndKeeps365Days()
    {
        var kept = Condition(AgentConditionStatus.Escalated, DaysAgo(500));
        kept.EscalatedAtUtc = DaysAgo(100);
        var expired = Condition(AgentConditionStatus.Escalated, DaysAgo(10));
        expired.EscalatedAtUtc = DaysAgo(400);

        Assert.That(Eligible(kept), Is.False);
        Assert.That(Eligible(expired), Is.True);
        Assert.That(Eligible(Condition(AgentConditionStatus.Escalated, DaysAgo(366))), Is.True);
    }

    [TestCase(AgentConditionStatus.Detected)]
    [TestCase(AgentConditionStatus.Reported)]
    [TestCase(AgentConditionStatus.Prepared)]
    public void OpenStatuses_AreNeverEligible(AgentConditionStatus status)
    {
        Assert.That(Eligible(Condition(status, DaysAgo(2000))), Is.False);
    }

    [Test]
    public void Dispatch_RequiresCreationReadAndReactionAllOlderThanCutoff()
    {
        var cutoff = AgentLedgerRetentionPolicy.DispatchCutoff(Now);
        var eligible = AgentLedgerRetentionPolicy.DispatchEligible(cutoff).Compile();

        var neverTouched = new ProactiveTriggerDispatchRow { CreateTime = DaysAgo(181) };
        var recentlyCreated = new ProactiveTriggerDispatchRow { CreateTime = DaysAgo(179) };
        var recentlyRead = new ProactiveTriggerDispatchRow { CreateTime = DaysAgo(300), ReadAtUtc = DaysAgo(10) };
        var recentlyReacted = new ProactiveTriggerDispatchRow { CreateTime = DaysAgo(300), ReactionAtUtc = DaysAgo(10) };
        var oldReadAndReaction = new ProactiveTriggerDispatchRow
        {
            CreateTime = DaysAgo(300),
            ReadAtUtc = DaysAgo(250),
            ReactionAtUtc = DaysAgo(200)
        };
        var unknownCreation = new ProactiveTriggerDispatchRow();

        Assert.That(eligible(neverTouched), Is.True);
        Assert.That(eligible(recentlyCreated), Is.False);
        Assert.That(eligible(recentlyRead), Is.False);
        Assert.That(eligible(recentlyReacted), Is.False);
        Assert.That(eligible(oldReadAndReaction), Is.True);
        Assert.That(eligible(unknownCreation), Is.False);
    }

    [Test]
    public async Task FakeRepository_SoftDeletesExpiredConditionsTogetherWithTheirEvents()
    {
        var repository = new FakeAgentConditionRepository();
        var expired = repository.Seed("kind", "kind:expired", AgentConditionStatus.Resolved, DaysAgo(200));
        expired.ResolvedAtUtc = DaysAgo(150);
        var fresh = repository.Seed("kind", "kind:fresh", AgentConditionStatus.Resolved, DaysAgo(20));
        fresh.ResolvedAtUtc = DaysAgo(10);
        var open = repository.Seed("kind", "kind:open", AgentConditionStatus.Reported, DaysAgo(900));

        var expiredEvent = new AgentConditionEvent { ConditionId = expired.Id, EventType = "Resolved" };
        var freshEvent = new AgentConditionEvent { ConditionId = fresh.Id, EventType = "Resolved" };
        await repository.InsertEventAsync(expiredEvent);
        await repository.InsertEventAsync(freshEvent);

        var (conditions, events) = await repository.SoftDeleteExpiredAsync(
            AgentLedgerRetentionPolicy.ShortLivedConditionCutoff(Now),
            AgentLedgerRetentionPolicy.LongLivedConditionCutoff(Now),
            Now);

        Assert.That(conditions, Is.EqualTo(1));
        Assert.That(events, Is.EqualTo(1));
        Assert.That(expired.IsDeleted, Is.True);
        Assert.That(expired.DeletedTime, Is.EqualTo(Now));
        Assert.That(expiredEvent.IsDeleted, Is.True);
        Assert.That(fresh.IsDeleted, Is.False);
        Assert.That(freshEvent.IsDeleted, Is.False);
        Assert.That(open.IsDeleted, Is.False);
    }
}
