// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the DedupKey of NextPeriodAutoCommitBlockedTriggerEvent, which is also its ledger fingerprint.
/// Two properties are at stake and they pull in opposite directions: the key must stay EXACTLY what it
/// was for NewViolations, the only reason that ever fired before the reason suffix existed (a changed
/// spelling strands every row opened back then, because this kind is no IAgentConditionFingerprintSource
/// and its rows are never auto-resolved), and it must DIFFER per reason for everything else, so a
/// timeout does not swallow a later compliance block for the same group and period.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodAutoCommitBlockedTriggerEventTests
{
    private static readonly Guid GroupId = Guid.Parse("7c2f4b18-0000-0000-0000-0000000000aa");
    private static readonly Guid ScenarioId = Guid.Parse("7c2f4b18-0000-0000-0000-0000000000bb");
    private static readonly DateOnly PeriodStart = new(2026, 2, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 2, 28);
    private const string GroupName = "Bern";

    private static NextPeriodAutoCommitBlockedTriggerEvent Blocked(
        NextPeriodAutoCommitBlockReason reason, Guid? scenarioId = null) =>
        new(GroupId, GroupName, PeriodStart, PeriodEnd, scenarioId ?? ScenarioId, 0, reason);

    [Test]
    public void DedupKey_NewViolations_StillSpelledExactlyAsBeforeTheReasonSuffix()
    {
        var key = Blocked(NextPeriodAutoCommitBlockReason.NewViolations).DedupKey;

        Assert.That(key, Is.EqualTo($"{GroupId}:2026-02-01:commit-blocked"),
            "Deliberately compared against the literal old format, not against a helper: an open ledger "
            + "row written before the reason suffix existed can only be found again through this exact "
            + "spelling, and this kind's rows are never auto-resolved.");
    }

    [Test]
    public void DedupKey_TwoDifferentReasons_AreTwoDifferentFindings()
    {
        var timeout = Blocked(NextPeriodAutoCommitBlockReason.Timeout).DedupKey;
        var killSwitch = Blocked(NextPeriodAutoCommitBlockReason.KillSwitch).DedupKey;

        Assert.Multiple(() =>
        {
            Assert.That(timeout, Is.Not.EqualTo(killSwitch),
                "Sharing one key would let the first outcome suppress the notification of the second.");
            Assert.That(timeout, Is.Not.EqualTo(Blocked(NextPeriodAutoCommitBlockReason.NewViolations).DedupKey));
        });
    }

    [Test]
    public void DedupKey_Interrupted_SeparatesTwoDraftsOfTheSamePeriod()
    {
        var first = Blocked(NextPeriodAutoCommitBlockReason.Interrupted).DedupKey;
        var second = Blocked(NextPeriodAutoCommitBlockReason.Interrupted, Guid.NewGuid()).DedupKey;

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void DedupKey_EveryReason_StartsWithTheSharedCommitOutcomePrefix()
    {
        var prefix = NextPeriodAutoCommitBlockedTriggerEvent.CommitOutcomeDedupPrefix(GroupId, PeriodStart);

        foreach (var reason in Enum.GetValues<NextPeriodAutoCommitBlockReason>())
        {
            Assert.That(Blocked(reason).DedupKey, Does.StartWith(prefix),
                $"NextPeriodSchedulingDueDetector recognises an already-reported outcome by this prefix; "
                + $"'{reason}' would slip past it and be reported a second time as interrupted.");
        }
    }
}
