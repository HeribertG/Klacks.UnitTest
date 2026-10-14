// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ReplacementSlotStatusPolicy: accepted or applied covers the slot, an unanswered request keeps it open,
/// only-proposed is not-yet-asked, and all-negative answers mean nobody is available.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class ReplacementSlotStatusPolicyTests
{
    private static IReadOnlyCollection<(ReplacementRequestOutcome, bool)> Rows(params (ReplacementRequestOutcome, bool)[] rows) => rows;

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
}
