// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ReplacementSlotStatusPolicy: accepted or applied covers the slot, an unanswered request keeps it open,
/// only-proposed is not-yet-asked, all-negative answers mean nobody is available, and a move taken back no longer covers
/// the slot (MoveReversed) unless another candidate covers it or a new request is still open.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ReplacementSlotStatusPolicyTests
{
    private static IReadOnlyCollection<(ReplacementRequestOutcome, bool, bool)> Rows(params (ReplacementRequestOutcome Outcome, bool Applied)[] rows) =>
        rows.Select(r => (r.Outcome, r.Applied, false)).ToList();

    private static IReadOnlyCollection<(ReplacementRequestOutcome, bool, bool)> WithReversedMove(
        params (ReplacementRequestOutcome Outcome, bool Applied)[] others) =>
        [(ReplacementRequestOutcome.Accepted, true, true), .. others.Select(r => (r.Outcome, r.Applied, false))];

    [Test]
    public void AcceptedCandidate_CoversTheSlotEvenWhenOthersDeclined()
    {
        var status = ReplacementSlotStatusPolicy.Classify(Rows(
            (ReplacementRequestOutcome.Declined, false), (ReplacementRequestOutcome.Accepted, false)));

        Assert.That(status, Is.EqualTo(ReplacementSlotStatus.Covered));
    }

    [Test]
    public void AppliedReplacement_CoversTheSlotWithoutAnAnswer()
    {
        var status = ReplacementSlotStatusPolicy.Classify(Rows((ReplacementRequestOutcome.Proposed, true)));

        Assert.That(status, Is.EqualTo(ReplacementSlotStatus.Covered));
    }

    [Test]
    public void RequestedWithoutAnswer_IsAwaitingAnswer()
    {
        var status = ReplacementSlotStatusPolicy.Classify(Rows(
            (ReplacementRequestOutcome.Declined, false), (ReplacementRequestOutcome.Requested, false), (ReplacementRequestOutcome.Proposed, false)));

        Assert.That(status, Is.EqualTo(ReplacementSlotStatus.AwaitingAnswer));
    }

    [Test]
    public void OnlyProposed_IsNotYetAsked()
    {
        var status = ReplacementSlotStatusPolicy.Classify(Rows(
            (ReplacementRequestOutcome.Proposed, false), (ReplacementRequestOutcome.Proposed, false)));

        Assert.That(status, Is.EqualTo(ReplacementSlotStatus.NotYetAsked));
    }

    [Test]
    public void ProposedNextToADeclinedOrUnreachedCandidate_IsPartlyDeclinedSoTheRefusalStaysVisible()
    {
        Assert.That(
            ReplacementSlotStatusPolicy.Classify(Rows((ReplacementRequestOutcome.Proposed, false), (ReplacementRequestOutcome.Declined, false))),
            Is.EqualTo(ReplacementSlotStatus.PartlyDeclined));
        Assert.That(
            ReplacementSlotStatusPolicy.Classify(Rows((ReplacementRequestOutcome.Proposed, false), (ReplacementRequestOutcome.NotReached, false))),
            Is.EqualTo(ReplacementSlotStatus.PartlyDeclined));
    }

    [Test]
    public void AllDeclinedOrNotReached_IsNobodyAvailable()
    {
        var status = ReplacementSlotStatusPolicy.Classify(Rows(
            (ReplacementRequestOutcome.Declined, false), (ReplacementRequestOutcome.NotReached, false)));

        Assert.That(status, Is.EqualTo(ReplacementSlotStatus.NobodyAvailable));
    }

    [Test]
    public void ReversedMove_IsNotCoveredButMoveReversed()
    {
        Assert.That(ReplacementSlotStatusPolicy.Classify(WithReversedMove()), Is.EqualTo(ReplacementSlotStatus.MoveReversed));
        Assert.That(
            ReplacementSlotStatusPolicy.Classify(WithReversedMove((ReplacementRequestOutcome.Declined, false))),
            Is.EqualTo(ReplacementSlotStatus.MoveReversed));
    }

    [Test]
    public void ReversedMove_NextToAnotherAcceptedCandidate_IsCovered()
    {
        Assert.That(
            ReplacementSlotStatusPolicy.Classify(WithReversedMove((ReplacementRequestOutcome.Accepted, false))),
            Is.EqualTo(ReplacementSlotStatus.Covered));
    }

    [Test]
    public void ReversedMove_NextToANewUnansweredRequest_IsAwaitingAnswer()
    {
        Assert.That(
            ReplacementSlotStatusPolicy.Classify(WithReversedMove((ReplacementRequestOutcome.Requested, false))),
            Is.EqualTo(ReplacementSlotStatus.AwaitingAnswer));
    }
}
