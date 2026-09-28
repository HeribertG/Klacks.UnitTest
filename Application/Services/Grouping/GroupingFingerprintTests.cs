// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the fingerprint contract: order-independent, report fingerprint only over report-only findings,
/// plan fingerprint over findings, proposals and subtree, prefix matching from the display length on.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingFingerprintTests
{
    private static readonly Guid ShiftA = Guid.NewGuid();
    private static readonly Guid ClientA = Guid.NewGuid();
    private static readonly Guid GroupA = Guid.NewGuid();

    private static readonly GroupingFinding Unfillable = new(
        GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: ShiftA, Reason: GroupingIneligibilityReason.NotShiftWorker);
    private static readonly GroupingFinding NoFit = new(
        GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: ClientA, Reason: GroupingIneligibilityReason.NoActiveContract);
    private static readonly GroupingFinding Covered = new(
        GroupingFindingCode.ShiftUncoveredInGroup, ReportOnly: false, GroupId: GroupA, ShiftId: ShiftA);
    private static readonly GroupingProposal Add = new(
        GroupingProposalKind.AddClient, GroupA, null, ClientA, null, GroupingFindingCode.ShiftUncoveredInGroup);

    [Test]
    public void SameContentInAnotherOrder_GivesTheSameFingerprint()
    {
        GroupingFingerprint.ForReport([Unfillable, NoFit]).ShouldBe(GroupingFingerprint.ForReport([NoFit, Unfillable]));
    }

    [Test]
    public void NonReportFinding_DoesNotChangeTheReportFingerprint()
    {
        GroupingFingerprint.ForReport([Unfillable, Covered]).ShouldBe(GroupingFingerprint.ForReport([Unfillable]));
    }

    [Test]
    public void Proposal_ChangesThePlanFingerprint()
    {
        GroupingFingerprint.ForPlan(null, [Unfillable, Covered], [Add])
            .ShouldNotBe(GroupingFingerprint.ForPlan(null, [Unfillable, Covered], []));
    }

    [Test]
    public void Subtree_ChangesThePlanFingerprint()
    {
        GroupingFingerprint.ForPlan(GroupA, [Unfillable], []).ShouldNotBe(GroupingFingerprint.ForPlan(null, [Unfillable], []));
    }

    [Test]
    public void Matches_AcceptsTheDisplayPrefixAndRejectsShortOrWrongCodes()
    {
        var fingerprint = GroupingFingerprint.ForReport([Unfillable]);
        var prefix = GroupingFingerprint.Shorten(fingerprint);

        prefix.Length.ShouldBe(GroupingFeasibilityDefaults.FingerprintDisplayLength);
        GroupingFingerprint.Matches(fingerprint, prefix.ToUpperInvariant()).ShouldBeTrue();
        GroupingFingerprint.Matches(fingerprint, fingerprint).ShouldBeTrue();
        GroupingFingerprint.Matches(fingerprint, prefix[..6]).ShouldBeFalse();
        GroupingFingerprint.Matches(fingerprint, new string('0', GroupingFeasibilityDefaults.FingerprintDisplayLength)).ShouldBeFalse();
    }
}
