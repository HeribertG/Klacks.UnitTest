// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what a group-restricted caller sees of a grouping report: groups are in scope over their Parent
/// ancestry even when the nested-set Root is empty, findings about clients or shifts that only live in
/// foreign groups are hidden, findings about ungrouped duties and employees stay visible (like the plan
/// view), and the name of a foreign target group is withheld while its proposal is hidden. A proposal that
/// would add an employee who lives only in foreign groups is hidden as well, and the counts of the view and
/// of the requester snapshot cover only what the caller may see.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupingScopeVisibilityTests
{
    private static readonly DateOnly From = new(2026, 9, 27);
    private static readonly Guid VisibleRootId = Guid.NewGuid();
    private static readonly Guid ChildId = Guid.NewGuid();
    private static readonly Guid ForeignRootId = Guid.NewGuid();
    private static readonly Guid ForeignClientId = Guid.NewGuid();
    private static readonly Guid UngroupedClientId = Guid.NewGuid();
    private static readonly Guid ChildShiftId = Guid.NewGuid();
    private static readonly Guid UngroupedShiftId = Guid.NewGuid();

    private static readonly GroupScopeAccess Restricted = GroupScopeAccess.Restricted([VisibleRootId], ["Region"]);

    private static GroupingFeasibilityReport Report(IReadOnlyList<GroupingFinding> findings, IReadOnlyList<GroupingProposal>? proposals = null) =>
        new(new GroupingAnalysisRequest(From, From.AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), null),
            findings,
            proposals ?? [],
            new string('a', 64),
            "r",
            new Dictionary<Guid, string> { [VisibleRootId] = "Region", [ChildId] = "Ward", [ForeignRootId] = "Foreign" },
            new Dictionary<Guid, IReadOnlyList<Guid>>
            {
                [VisibleRootId] = [VisibleRootId],
                [ChildId] = [ChildId, VisibleRootId],
                [ForeignRootId] = [ForeignRootId],
            },
            new Dictionary<Guid, IReadOnlyList<Guid>>
            {
                [ForeignClientId] = [ForeignRootId],
                [ChildShiftId] = [ChildId],
            },
            new Dictionary<Guid, string> { [ForeignClientId] = "Foreign Person", [UngroupedClientId] = "Free Person" },
            new Dictionary<Guid, string> { [ChildShiftId] = "Ward Early", [UngroupedShiftId] = "Loose Late" },
            2,
            2);

    [Test]
    public void ClientThatFitsNoShift_LivingOnlyInAForeignGroup_IsHidden()
    {
        var finding = new GroupingFinding(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: ForeignClientId);

        GroupingScopeVisibility.IsFindingVisible(Report([finding]), Restricted, finding).ShouldBeFalse();
    }

    [Test]
    public void ClientThatFitsNoShift_WithoutAnyGroup_StaysVisible()
    {
        var finding = new GroupingFinding(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: UngroupedClientId);

        GroupingScopeVisibility.IsFindingVisible(Report([finding]), Restricted, finding).ShouldBeTrue();
    }

    [Test]
    public void UnfillableShift_InAChildGroupWithoutNestedSetRoot_IsVisibleOverItsParent()
    {
        var finding = new GroupingFinding(GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: ChildShiftId);

        GroupingScopeVisibility.IsFindingVisible(Report([finding]), Restricted, finding).ShouldBeTrue();
    }

    [Test]
    public void DeadMembership_InAForeignGroup_IsHidden()
    {
        var finding = new GroupingFinding(GroupingFindingCode.ClientDeadMembership, ReportOnly: true, GroupId: ForeignRootId, ClientId: ForeignClientId);

        GroupingScopeVisibility.IsFindingVisible(Report([finding]), Restricted, finding).ShouldBeFalse();
    }

    [Test]
    public void UngroupedShift_WithAForeignTargetGroup_StaysVisibleWithoutTheGroupName_AndItsProposalIsHidden()
    {
        var finding = new GroupingFinding(GroupingFindingCode.ShiftWithoutGroup, ReportOnly: false, GroupId: ForeignRootId, ShiftId: UngroupedShiftId);
        var proposal = new GroupingProposal(GroupingProposalKind.AddShift, ForeignRootId, null, null, UngroupedShiftId, GroupingFindingCode.ShiftWithoutGroup);

        var view = GroupingReportViewBuilder.Build(Report([finding], [proposal]), Restricted, null);

        var shown = view.Findings.ShouldHaveSingleItem();
        shown.Shift.ShouldBe("Loose Late");
        shown.Group.ShouldBeNull();
        view.Proposals.ShouldBeEmpty();
    }

    [Test]
    public void ReportView_ForARestrictedUser_ShowsOnlyTheVisibleFindings()
    {
        IReadOnlyList<GroupingFinding> findings =
        [
            new(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: ForeignClientId),
            new(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: UngroupedClientId),
            new(GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: ChildShiftId),
        ];

        var view = GroupingReportViewBuilder.Build(Report(findings), Restricted, null);

        view.Findings.Select(finding => finding.Client ?? finding.Shift).ShouldBe(["Free Person", "Ward Early"], ignoreOrder: true);
    }

    [Test]
    public void Unrestricted_SeesEverything()
    {
        var finding = new GroupingFinding(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: ForeignClientId);

        GroupingScopeVisibility.IsFindingVisible(Report([finding]), GroupScopeAccess.Unrestricted(), finding).ShouldBeTrue();
    }

    [Test]
    public void GroupList_ForARestrictedUser_KeepsChildrenWithoutNestedSetRoot_AndDropsForeignGroups()
    {
        var groups = new List<Group>
        {
            new() { Id = VisibleRootId, Name = "Region" },
            new() { Id = ChildId, Name = "Ward", Parent = VisibleRootId, Root = null },
            new() { Id = ForeignRootId, Name = "Foreign" },
        };

        GroupingScopeVisibility.FilterByLineage(groups, Restricted).Select(group => group.Id)
            .ShouldBe([VisibleRootId, ChildId], ignoreOrder: true);
    }

    [Test]
    public void ReportView_ForARestrictedUser_HidesTheAdditionOfAForeignEmployeeToAVisibleGroup()
    {
        var proposal = new GroupingProposal(GroupingProposalKind.AddClient, ChildId, null, ForeignClientId, null, GroupingFindingCode.ShiftUncoveredInGroup);

        var view = GroupingReportViewBuilder.Build(Report([], [proposal]), Restricted, null);

        view.Proposals.ShouldBeEmpty();
        view.Counts.Proposals.ShouldBe(0);
        System.Text.Json.JsonSerializer.Serialize(view).ShouldNotContain("Foreign Person");
    }

    [Test]
    public void ReportView_ForARestrictedUser_CountsOnlyTheVisibleFindingsProposalsAndMembers()
    {
        var view = GroupingReportViewBuilder.Build(MixedReport(), Restricted, null);

        view.Counts.ShouldBe(new GroupingFeasibilityCounts(UnfillableShifts: 1, UnmatchedClients: 1, CapacityShortfalls: 0, Proposals: 1));
        view.AnalysedEmployees.ShouldBe(1);
        view.AnalysedDuties.ShouldBe(2);
    }

    [Test]
    public void ReportView_ForAnUnrestrictedUser_CountsTheWholeAnalysis()
    {
        var report = MixedReport();

        var view = GroupingReportViewBuilder.Build(report, GroupScopeAccess.Unrestricted(), null);

        view.Counts.ShouldBe(GroupingFeasibilityCounts.From(report));
        view.AnalysedEmployees.ShouldBe(report.AnalysedClientCount);
        view.AnalysedDuties.ShouldBe(report.AnalysedShiftCount);
    }

    [Test]
    public void RequesterSnapshot_ForARestrictedUser_CoversOnlyTheVisibleReportFindings()
    {
        var report = MixedReport();
        var visibleReportFindings = report.Findings.Where(finding => GroupingScopeVisibility.IsFindingVisible(report, Restricted, finding));

        var snapshot = GroupingScopeVisibility.SnapshotFor(report, Restricted);

        snapshot.Counts.ShouldBe(new GroupingFeasibilityCounts(1, 1, 0, 1));
        snapshot.HasReportFindings.ShouldBeTrue();
        snapshot.ReportFingerprint.ShouldBe(GroupingFingerprint.ForReport(visibleReportFindings));
        snapshot.ReportFingerprint.ShouldNotBe(report.ReportFingerprint);
    }

    [Test]
    public void RequesterSnapshot_ForARestrictedUser_WithOnlyForeignReportFindings_HasNoReportFindings()
    {
        var report = Report([new GroupingFinding(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: ForeignClientId)]);

        var snapshot = GroupingScopeVisibility.SnapshotFor(report, Restricted);

        snapshot.HasReportFindings.ShouldBeFalse();
        snapshot.Counts.UnmatchedClients.ShouldBe(0);
    }

    [Test]
    public void RequesterSnapshot_ForAnUnrestrictedUser_IsTheWholeReport()
    {
        var report = MixedReport();

        GroupingScopeVisibility.SnapshotFor(report, GroupScopeAccess.Unrestricted()).ShouldBe(GroupingFeasibilityDailySnapshot.From(report));
    }

    private static GroupingFeasibilityReport MixedReport()
    {
        IReadOnlyList<GroupingFinding> findings =
        [
            new(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: ForeignClientId),
            new(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: UngroupedClientId),
            new(GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: ChildShiftId),
            new(GroupingFindingCode.CapacityShortfall, ReportOnly: true, GroupId: ForeignRootId, Weekday: DayOfWeek.Monday, Demand: 3, Supply: 1),
        ];
        IReadOnlyList<GroupingProposal> proposals =
        [
            new(GroupingProposalKind.AddShift, ChildId, null, null, UngroupedShiftId, GroupingFindingCode.ShiftWithoutGroup),
            new(GroupingProposalKind.AddClient, ForeignRootId, null, UngroupedClientId, null, GroupingFindingCode.ClientWithoutGroup),
            new(GroupingProposalKind.AddClient, ChildId, null, ForeignClientId, null, GroupingFindingCode.ShiftUncoveredInGroup),
        ];
        return Report(findings, proposals) with { ReportFingerprint = GroupingFingerprint.ForReport(findings) };
    }
}
