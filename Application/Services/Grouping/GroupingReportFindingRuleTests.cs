// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins plan decision D15: inbox/report findings are exactly F1, F5, F6 only in its report-only case,
/// and F7. F2, F3 and F4 never count, whatever their ReportOnly flag says. The report flag and the
/// report fingerprint both follow this rule.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingReportFindingRuleTests
{
    private static readonly Guid ShiftA = Guid.NewGuid();
    private static readonly Guid ClientA = Guid.NewGuid();
    private static readonly Guid GroupA = Guid.NewGuid();

    private static readonly GroupingFinding Unfillable = new(
        GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: ShiftA, Reason: GroupingIneligibilityReason.NotShiftWorker);

    [TestCase(GroupingFindingCode.ShiftUnfillableGlobally, true, true)]
    [TestCase(GroupingFindingCode.ShiftUnfillableGlobally, false, true)]
    [TestCase(GroupingFindingCode.ShiftUncoveredInGroup, true, false)]
    [TestCase(GroupingFindingCode.ShiftUncoveredInGroup, false, false)]
    [TestCase(GroupingFindingCode.ShiftWithoutGroup, true, false)]
    [TestCase(GroupingFindingCode.ShiftWithoutGroup, false, false)]
    [TestCase(GroupingFindingCode.ClientWithoutGroup, true, false)]
    [TestCase(GroupingFindingCode.ClientWithoutGroup, false, false)]
    [TestCase(GroupingFindingCode.ClientFitsNoShift, true, true)]
    [TestCase(GroupingFindingCode.ClientFitsNoShift, false, true)]
    [TestCase(GroupingFindingCode.ClientDeadMembership, true, true)]
    [TestCase(GroupingFindingCode.ClientDeadMembership, false, false)]
    [TestCase(GroupingFindingCode.CapacityShortfall, true, true)]
    [TestCase(GroupingFindingCode.CapacityShortfall, false, true)]
    public void IsReportFinding_FollowsD15(GroupingFindingCode code, bool reportOnly, bool expected)
    {
        GroupingFinding.IsReportFinding(new GroupingFinding(code, reportOnly)).ShouldBe(expected);
    }

    [TestCase(GroupingFindingCode.ShiftUncoveredInGroup)]
    [TestCase(GroupingFindingCode.ClientWithoutGroup)]
    public void ReportFingerprint_IgnoresF2AndF4EvenWhenFlaggedReportOnly(GroupingFindingCode code)
    {
        var flagged = new GroupingFinding(code, ReportOnly: true, GroupId: GroupA, ShiftId: ShiftA, ClientId: ClientA);

        GroupingFingerprint.ForReport([Unfillable, flagged]).ShouldBe(GroupingFingerprint.ForReport([Unfillable]));
    }

    [Test]
    public void ReportFingerprint_CountsF6OnlyInItsReportOnlyCase()
    {
        var removed = new GroupingFinding(GroupingFindingCode.ClientDeadMembership, ReportOnly: false, GroupId: GroupA, ClientId: ClientA);
        var reported = removed with { ReportOnly = true };

        GroupingFingerprint.ForReport([Unfillable, removed]).ShouldBe(GroupingFingerprint.ForReport([Unfillable]));
        GroupingFingerprint.ForReport([Unfillable, reported]).ShouldNotBe(GroupingFingerprint.ForReport([Unfillable]));
    }

    [Test]
    public void HasReportFindings_IsFalseForAnF4FlaggedReportOnly()
    {
        var flagged = new GroupingFinding(GroupingFindingCode.ClientWithoutGroup, ReportOnly: true, ClientId: ClientA);

        Report(flagged).HasReportFindings.ShouldBeFalse();
    }

    [Test]
    public void HasReportFindings_IsTrueForAReportOnlyF6()
    {
        var reported = new GroupingFinding(GroupingFindingCode.ClientDeadMembership, ReportOnly: true, GroupId: GroupA, ClientId: ClientA);

        Report(reported).HasReportFindings.ShouldBeTrue();
    }

    private static GroupingFeasibilityReport Report(params GroupingFinding[] findings) => new(
        new GroupingAnalysisRequest(new DateOnly(2026, 9, 28), new DateOnly(2026, 11, 23), null),
        findings,
        [],
        string.Empty,
        string.Empty,
        new Dictionary<Guid, string>(),
        new Dictionary<Guid, IReadOnlyList<Guid>>(),
        new Dictionary<Guid, IReadOnlyList<Guid>>(),
        new Dictionary<Guid, string>(),
        new Dictionary<Guid, string>(),
        0,
        0);
}
