// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the read-only grouping check skill: default period, names and sentences instead of ids and
/// codes, the plan code, notification of the requester, rejection of an oversized period before any
/// analysis, a text for every finding the plan builder can emit, and a group-restricted requester being
/// notified with the scoped snapshot instead of the whole installation. The summary states the analysed
/// period in ISO and display form and one explicit count per proposal kind, zeros included, so the model
/// has nothing to infer (it once reported removals that were not proposed and mistyped the end year).
/// A report with proposals is recorded as the user's preview of the full plan in the current turn, so the
/// apply skill refuses apply=true in the analysis turn and accepts the Yes chip's parameters (plan code and
/// period, no start date) in the user's next turn: a removal-only plan is applied, and a plan that adds
/// members passes the preview gate and applies too, defaulting the start date to the start of the checked
/// period. A report without proposals records nothing.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AnalyzeGroupingFeasibilitySkillTests
{
    private static readonly DateTime Today = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
    private const int CacheSizeLimit = 100;
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly string FullPlanFingerprint = new('a', 64);

    private IGroupingFeasibilityAnalyzer _analyzer = null!;
    private IGroupingFeasibilityNotifier _notifier = null!;
    private IGroupRepository _groups = null!;
    private ICompanyClock _clock = null!;
    private MemoryCache _cache = null!;
    private GroupingPlanPreviewRegistry _previews = null!;
    private AnalyzeGroupingFeasibilitySkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _analyzer = Substitute.For<IGroupingFeasibilityAnalyzer>();
        _notifier = Substitute.For<IGroupingFeasibilityNotifier>();
        _groups = Substitute.For<IGroupRepository>();
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimit });
        _previews = new GroupingPlanPreviewRegistry(_cache);
        _skill = new AnalyzeGroupingFeasibilitySkill(
            _analyzer, _notifier, _groups, TestGroupScopeGuard.Unrestricted(), _clock, _previews,
            NullLogger<AnalyzeGroupingFeasibilitySkill>.Instance);
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { Permissions.CanViewGroups }
    };

    private static GroupingFeasibilityReport Report(GroupingAnalysisRequest request)
    {
        IReadOnlyList<GroupingFinding> findings =
        [
            new(GroupingFindingCode.ShiftUnfillableGlobally, ReportOnly: true, ShiftId: ShiftId,
                Reason: GroupingIneligibilityReason.NotShiftWorker,
                ReasonCounts: [new GroupingReasonCount(GroupingIneligibilityReason.NotShiftWorker, 3)]),
        ];
        return new GroupingFeasibilityReport(
            request, findings, [], new string('a', 64), GroupingFingerprint.ForReport(findings),
            new Dictionary<Guid, string>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, IReadOnlyList<Guid>>(), new Dictionary<Guid, string>(),
            new Dictionary<Guid, string> { [ShiftId] = "Nachtdienst" }, 3, 1);
    }

    [Test]
    public async Task DefaultPeriod_ReturnsNamesAndSentences_AndNotifies()
    {
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Report(call.Arg<GroupingAnalysisRequest>()));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var view = result.Data.ShouldBeOfType<GroupingReportView>();
        view.From.ShouldBe("2026-09-27");
        view.Until.ShouldBe("2026-11-22");
        view.PlanCode.ShouldBe(new string('a', GroupingFeasibilityDefaults.FingerprintDisplayLength));
        view.Findings.ShouldHaveSingleItem().Shift.ShouldBe("Nachtdienst");
        view.Findings[0].Reason.ShouldNotContain(nameof(GroupingIneligibilityReason.NotShiftWorker));
        result.Message.ShouldContain("Nothing was changed");
        await _notifier.Received(1).NotifyAsync(
            Arg.Any<GroupingFeasibilityReport>(), Arg.Any<GroupingFeasibilityDailySnapshot>(), Arg.Any<Guid>(), false, Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OversizedPeriod_IsRejectedBeforeAnalysis()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            [GroupingSkillInputs.FromDateParameter] = "2026-01-01",
            [GroupingSkillInputs.UntilDateParameter] = "2027-12-31",
        });

        result.Success.ShouldBeFalse();
        await _analyzer.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, default);
    }

    [Test]
    public void EveryFindingCombinationTheBuilderEmits_HasAText()
    {
        (GroupingFindingCode, bool)[] emitted =
        [
            (GroupingFindingCode.ShiftUnfillableGlobally, true),
            (GroupingFindingCode.ShiftUncoveredInGroup, false),
            (GroupingFindingCode.ShiftUncoveredInGroup, true),
            (GroupingFindingCode.ShiftWithoutGroup, false),
            (GroupingFindingCode.ClientWithoutGroup, false),
            (GroupingFindingCode.ClientWithoutGroup, true),
            (GroupingFindingCode.ClientFitsNoShift, true),
            (GroupingFindingCode.ClientDeadMembership, false),
            (GroupingFindingCode.ClientDeadMembership, true),
            (GroupingFindingCode.CapacityShortfall, true),
        ];

        emitted.ShouldAllBe(key => GroupingReportTexts.Findings.ContainsKey(key));
        Enum.GetValues<GroupingIneligibilityReason>().ShouldAllBe(reason => GroupingReportTexts.Reasons.ContainsKey(reason));
        Enum.GetValues<GroupingProposalKind>().ShouldAllBe(kind => GroupingReportTexts.Actions.ContainsKey(kind));
    }

    [Test]
    public async Task RestrictedUser_CanNameAChildGroupWhoseNestedSetRootIsEmpty()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _groups.List().Returns(new List<Group>
        {
            new() { Id = rootId, Name = "Region" },
            new() { Id = childId, Name = "Ward", Parent = rootId, Root = null },
            new() { Id = Guid.NewGuid(), Name = "Ward North", Parent = Guid.NewGuid(), Root = null },
        });
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Report(call.Arg<GroupingAnalysisRequest>()));
        var skill = new AnalyzeGroupingFeasibilitySkill(
            _analyzer, _notifier, _groups, TestGroupScopeGuard.Restricted([rootId], "Region"), _clock, _previews,
            NullLogger<AnalyzeGroupingFeasibilitySkill>.Instance);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { [GroupingSkillInputs.GroupNameParameter] = "Ward" });

        result.Success.ShouldBeTrue();
        await _analyzer.Received(1).AnalyzeAsync(
            Arg.Is<GroupingAnalysisRequest>(request => request.ScopeGroupId == childId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedUser_IsNotifiedWithTheScopedSnapshot_NotTheWholeInstallation()
    {
        var rootId = Guid.NewGuid();
        var foreignGroupId = Guid.NewGuid();
        var foreignClientId = Guid.NewGuid();
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                IReadOnlyList<GroupingFinding> findings =
                    [new(GroupingFindingCode.ClientFitsNoShift, ReportOnly: true, ClientId: foreignClientId)];
                return Report(call.Arg<GroupingAnalysisRequest>()) with
                {
                    Findings = findings,
                    ReportFingerprint = GroupingFingerprint.ForReport(findings),
                    GroupLineage = new Dictionary<Guid, IReadOnlyList<Guid>> { [rootId] = [rootId], [foreignGroupId] = [foreignGroupId] },
                    MemberGroups = new Dictionary<Guid, IReadOnlyList<Guid>> { [foreignClientId] = [foreignGroupId] },
                };
            });
        var skill = new AnalyzeGroupingFeasibilitySkill(
            _analyzer, _notifier, _groups, TestGroupScopeGuard.Restricted([rootId], "Region"), _clock, _previews,
            NullLogger<AnalyzeGroupingFeasibilitySkill>.Instance);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await _notifier.Received(1).NotifyAsync(
            Arg.Any<GroupingFeasibilityReport>(),
            Arg.Is<GroupingFeasibilityDailySnapshot>(snapshot => !snapshot.HasReportFindings && snapshot.Counts.UnmatchedClients == 0),
            Arg.Any<Guid>(),
            false,
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Summary_StatesPeriodInIsoAndDisplayForm_AndEveryProposalKindCountIncludingZeros()
    {
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        IReadOnlyList<GroupingProposal> proposals =
        [
            new(GroupingProposalKind.AddShift, groupId, null, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup),
            new(GroupingProposalKind.AddShift, groupId, null, null, Guid.NewGuid(), GroupingFindingCode.ShiftWithoutGroup),
            new(GroupingProposalKind.AddClient, groupId, null, clientId, null, GroupingFindingCode.ShiftUncoveredInGroup),
        ];
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Report(call.Arg<GroupingAnalysisRequest>()) with { Proposals = proposals });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var view = result.Data.ShouldBeOfType<GroupingReportView>();
        view.ProposalCounts.ShouldBe(new GroupingProposalKindCounts(NewGroups: 0, DutiesAdded: 2, EmployeesAdded: 1, EmployeesRemoved: 0));
        view.Period.ShouldBe("2026-09-27 to 2026-11-22 (27.09.2026 to 22.11.2026)");
        result.Message.ShouldContain("2026-09-27 to 2026-11-22 (27.09.2026 to 22.11.2026)");
        result.Message.ShouldContain("3 proposed group changes: 0 new groups, 2 duties added to a group, 1 employees added to a group, 0 removals of employees from a group");
    }

    [Test]
    public async Task Summary_WithoutProposals_StillStatesZeroRemovals()
    {
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Report(call.Arg<GroupingAnalysisRequest>()));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain("0 removals of employees from a group");
    }

    private void GivenReportWithProposal(GroupingProposalKind kind)
    {
        var groupId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        IReadOnlyList<GroupingProposal> proposals =
            [new(kind, groupId, null, clientId, null, GroupingFindingCode.ShiftUncoveredInGroup)];
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Report(call.Arg<GroupingAnalysisRequest>()) with
            {
                Proposals = proposals,
                GroupNames = new Dictionary<Guid, string> { [groupId] = "Pflege" },
                GroupLineage = new Dictionary<Guid, IReadOnlyList<Guid>> { [groupId] = [groupId] },
                ClientNames = new Dictionary<Guid, string> { [clientId] = "Anna Muster" },
            });
    }

    [Test]
    public async Task ReportWithProposals_IsRecordedAsTheUsersPreviewInTheCurrentTurn()
    {
        GivenReportWithProposal(GroupingProposalKind.AddClient);
        var analysisTurn = Ctx() with { TurnId = Guid.NewGuid() };

        (await _skill.ExecuteAsync(analysisTurn, new Dictionary<string, object>())).Success.ShouldBeTrue();

        _previews.GetPreviewStatus(analysisTurn.UserId, FullPlanFingerprint, analysisTurn.TurnId).ShouldBe(GroupingPreviewStatus.SameTurn);
        _previews.GetPreviewStatus(analysisTurn.UserId, FullPlanFingerprint, Guid.NewGuid()).ShouldBe(GroupingPreviewStatus.Confirmable);
        _previews.GetPreviewStatus(Guid.NewGuid(), FullPlanFingerprint, Guid.NewGuid()).ShouldBe(GroupingPreviewStatus.Missing);
    }

    [Test]
    public async Task ReportWithoutProposals_RecordsNoPreview()
    {
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Report(call.Arg<GroupingAnalysisRequest>()));
        var context = Ctx() with { TurnId = Guid.NewGuid() };

        await _skill.ExecuteAsync(context, new Dictionary<string, object>());

        _previews.GetPreviewStatus(context.UserId, FullPlanFingerprint, Guid.NewGuid()).ShouldBe(GroupingPreviewStatus.Missing);
    }

    [Test]
    public async Task YesChipOfARemovalOnlyPlan_AppliesInTheNextTurnWithoutASecondPreview_ButNotInTheAnalysisTurn()
    {
        GivenReportWithProposal(GroupingProposalKind.RemoveClient);
        var (applySkill, applier) = ApplySkillSharingThePreviews();
        var analysisTurn = AdminTurn();
        var chip = ChipParameters(await _skill.ExecuteAsync(analysisTurn, new Dictionary<string, object>()));

        var sameTurn = await applySkill.ExecuteAsync(analysisTurn, chip);
        var nextTurn = await applySkill.ExecuteAsync(analysisTurn with { TurnId = Guid.NewGuid() }, chip);

        sameTurn.Success.ShouldBeFalse();
        sameTurn.Message!.ShouldContain(ApplyGroupingPlanSkill.SameTurnMarker);
        nextTurn.Success.ShouldBeTrue();
        await applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task YesChipOfAPlanThatAddsMembers_PassesThePreviewGateInTheNextTurn_AndDefaultsTheStartDateToThePeriodStart()
    {
        GivenReportWithProposal(GroupingProposalKind.AddClient);
        var (applySkill, applier) = ApplySkillSharingThePreviews();
        var analysisTurn = AdminTurn();
        var chip = ChipParameters(await _skill.ExecuteAsync(analysisTurn, new Dictionary<string, object>()));

        var nextTurn = await applySkill.ExecuteAsync(analysisTurn with { TurnId = Guid.NewGuid() }, chip);

        nextTurn.Success.ShouldBeTrue();
        await applier.Received(1).ApplyAsync(
            Arg.Is<GroupingApplyCommand>(command => command.ValidFromUtc == Today),
            Arg.Any<CancellationToken>());
    }

    private static SkillExecutionContext AdminTurn() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { Roles.Admin },
        TurnId = Guid.NewGuid(),
    };

    private static Dictionary<string, object> ChipParameters(SkillResult check)
    {
        var view = check.Data.ShouldBeOfType<GroupingReportView>();
        return new Dictionary<string, object>
        {
            [ApplyGroupingPlanSkill.FingerprintParameter] = view.PlanCode,
            [ApplyGroupingPlanSkill.ApplyParameter] = true,
            [GroupingSkillInputs.FromDateParameter] = view.From,
            [GroupingSkillInputs.UntilDateParameter] = view.Until,
        };
    }

    private (ApplyGroupingPlanSkill Skill, IGroupingPlanApplier Applier) ApplySkillSharingThePreviews()
    {
        var applier = Substitute.For<IGroupingPlanApplier>();
        applier.ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>())
            .Returns(new GroupingApplyResult(null, 0, 0, 0, 1, 0));
        var skill = new ApplyGroupingPlanSkill(
            _analyzer, applier, _groups, TestGroupScopeGuard.Unrestricted(), _clock,
            Substitute.For<IGroupingFeasibilityDailySnapshotStore>(), _previews, NullLogger<ApplyGroupingPlanSkill>.Instance);
        return (skill, applier);
    }
}
