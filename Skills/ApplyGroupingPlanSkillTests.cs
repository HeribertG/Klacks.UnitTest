// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the apply skill: preview by default without writing, stale plan code rejected, changes the user
/// may not make skipped while the rest is applied, an omitted start date defaults to the start of the
/// checked period (stated in the result) instead of being asked for, a failed database verification
/// reported as an error, and the daily snapshot dropped only
/// after a successful apply. The permission cases pin the plan dependencies: when the new group may not
/// be created, no change that targets it reaches the applier (which would reject such a plan), and a
/// removal is skipped when the same employee's addition elsewhere is skipped. A group-restricted user may
/// not add an employee whose groups all lie outside the scope (that would reveal the person), even into a
/// group inside the scope; ungrouped employees and employees with one group in scope stay permitted.
/// Server-side preview gate: apply=true is refused unless the same user previewed the same recomputed
/// plan before (a fresh conversation cannot jump straight to apply), a preview by another user does not
/// count, and a successful apply consumes the preview. An apply in the same chat turn as the preview is
/// refused without writing and without consuming the preview, so it succeeds in the user's next turn,
/// also when the model repeats the preview in that confirming turn. After a successful apply the analysis
/// is recomputed and the remaining gaps per planning unit (F1, F2, F7 and blocking causes) are stated with
/// the sentence that they need master-data changes, so the model cannot claim everything is plannable; a
/// failed recompute never turns the committed apply into an error.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ApplyGroupingPlanSkillTests
{
    private static readonly DateTime Today = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid OtherRootGroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid SecondClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly string Fingerprint = new('b', 64);

    private IGroupingFeasibilityAnalyzer _analyzer = null!;
    private IGroupingPlanApplier _applier = null!;
    private IGroupingFeasibilityDailySnapshotStore _snapshotStore = null!;
    private ICompanyClock _clock = null!;
    private IGroupingPlanPreviewRegistry _previews = null!;
    private ApplyGroupingPlanSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _analyzer = Substitute.For<IGroupingFeasibilityAnalyzer>();
        _applier = Substitute.For<IGroupingPlanApplier>();
        _applier.ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>())
            .Returns(new GroupingApplyResult(null, 0, 0, 1, 0, 0));
        _snapshotStore = Substitute.For<IGroupingFeasibilityDailySnapshotStore>();
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _previews = Substitute.For<IGroupingPlanPreviewRegistry>();
        _previews.GetPreviewStatus(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>()).Returns(GroupingPreviewStatus.Confirmable);
        _skill = NewSkill(TestGroupScopeGuard.Unrestricted());
    }

    private ApplyGroupingPlanSkill NewSkill(IGroupScopeGuard scopeGuard) =>
        new(_analyzer, _applier, Substitute.For<IGroupRepository>(), scopeGuard, _clock, _snapshotStore, _previews,
            NullLogger<ApplyGroupingPlanSkill>.Instance);

    private ApplyGroupingPlanSkill SkillWithRealPreviewGate() =>
        new(_analyzer, _applier, Substitute.For<IGroupRepository>(), TestGroupScopeGuard.Unrestricted(), _clock, _snapshotStore,
            new GroupingPlanPreviewRegistry(new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 })),
            NullLogger<ApplyGroupingPlanSkill>.Instance);

    private static GroupingFeasibilityReport Plan(GroupingAnalysisRequest request, IReadOnlyList<GroupingProposal> proposals) =>
        new(request, [], proposals, Fingerprint, "r",
            new Dictionary<Guid, string> { [GroupId] = "Pflege", [OtherRootGroupId] = "Extern" },
            new Dictionary<Guid, IReadOnlyList<Guid>> { [GroupId] = [GroupId], [OtherRootGroupId] = [OtherRootGroupId] },
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            new Dictionary<Guid, string> { [ClientId] = "Anna Muster", [SecondClientId] = "Ben Beispiel" },
            new Dictionary<Guid, string> { [ShiftId] = "Früh" }, 1, 1);

    private void GivenPlan(params GroupingProposal[] proposals) =>
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Plan(call.Arg<GroupingAnalysisRequest>(), proposals));

    private static GroupingProposal AddClient(Guid? groupId = null, Guid? clientId = null) =>
        new(GroupingProposalKind.AddClient, groupId ?? GroupId, null, clientId ?? ClientId, null, GroupingFindingCode.ShiftUncoveredInGroup);

    private static GroupingProposal AddShift() => new(GroupingProposalKind.AddShift, GroupId, null, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup);

    private static GroupingProposal Remove(Guid? clientId = null) =>
        new(GroupingProposalKind.RemoveClient, GroupId, null, clientId ?? ClientId, null, GroupingFindingCode.ClientDeadMembership);

    private static GroupingProposal CreateGroup() =>
        new(GroupingProposalKind.CreateGroup, null, GroupingFeasibilityDefaults.NewGroupKey, null, null, GroupingFindingCode.ShiftWithoutGroup);

    private static GroupingProposal AddShiftToNewGroup() =>
        new(GroupingProposalKind.AddShift, null, GroupingFeasibilityDefaults.NewGroupKey, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup);

    private static GroupingProposal AddClientToNewGroup() =>
        new(GroupingProposalKind.AddClient, null, GroupingFeasibilityDefaults.NewGroupKey, SecondClientId, null, GroupingFindingCode.ClientWithoutGroup);

    private static SkillExecutionContext Ctx(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = permissions.ToList()
    };

    private static readonly string[] AllGroupRights =
        [Permissions.CanEditClients, Permissions.CanViewGroups, Permissions.CanEditShifts, Permissions.CanCreateGroups];

    private static Dictionary<string, object> Params(bool apply = false, string? validFrom = "2026-10-01", string? code = null, string? newGroupName = null)
    {
        var parameters = new Dictionary<string, object>
        {
            [ApplyGroupingPlanSkill.FingerprintParameter] = code ?? Fingerprint[..GroupingFeasibilityDefaults.FingerprintDisplayLength],
            [ApplyGroupingPlanSkill.ApplyParameter] = apply,
        };
        if (validFrom != null)
        {
            parameters[ApplyGroupingPlanSkill.ValidFromParameter] = validFrom;
        }

        if (newGroupName != null)
        {
            parameters[ApplyGroupingPlanSkill.NewGroupNameParameter] = newGroupName;
        }

        return parameters;
    }

    private static GroupingFeasibilityReport ReportOf(params GroupingProposal[] proposals) =>
        Plan(new GroupingAnalysisRequest(DateOnly.FromDateTime(Today), DateOnly.FromDateTime(Today).AddDays(GroupingFeasibilityDefaults.DefaultHorizonDays), null), proposals);

    [Test]
    public async Task Default_IsAPreviewAndWritesNothing()
    {
        GivenPlan(AddClient());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Nothing was changed yet");
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        _snapshotStore.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [Test]
    public async Task StalePlanCode_IsRejected()
    {
        GivenPlan(AddClient());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true, code: new string('c', 12)));

        result.Success.ShouldBeFalse();
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
    }

    [Test]
    public async Task MissingShiftRight_SkipsTheDutyAndAppliesTheEmployee()
    {
        GivenPlan(AddShift(), AddClient());

        var result = await _skill.ExecuteAsync(
            Ctx(Permissions.CanEditClients, Permissions.CanViewGroups), Params(apply: true));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(GroupingProposalPermissionFilter.MissingShiftRight);
        await _applier.Received(1).ApplyAsync(
            Arg.Is<GroupingApplyCommand>(command => command.Proposals.Count == 1 && command.Proposals[0].Kind == GroupingProposalKind.AddClient),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddingWithoutStartDate_UsesTheStartOfTheCheckedPeriod()
    {
        GivenPlan(AddClient());
        var periodStart = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var parameters = Params(apply: true, validFrom: null);
        parameters[GroupingSkillInputs.FromDateParameter] = "2026-10-05";

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2026-10-05");
        await _applier.Received(1).ApplyAsync(
            Arg.Is<GroupingApplyCommand>(command => command.ValidFromUtc == periodStart),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AddingWithAnExplicitStartDate_UsesItInsteadOfThePeriodStart()
    {
        GivenPlan(AddClient());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true, validFrom: "2026-10-01"));

        result.Success.ShouldBeTrue();
        await _applier.Received(1).ApplyAsync(
            Arg.Is<GroupingApplyCommand>(command => command.ValidFromUtc == new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PureRemoval_NeedsNoStartDate()
    {
        GivenPlan(Remove());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true, validFrom: null));

        result.Success.ShouldBeTrue();
        await _applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreatingAGroupWithoutName_IsRejected()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeFalse();
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
    }

    [Test]
    public async Task VerificationFailure_IsReportedAsError()
    {
        GivenPlan(AddClient());
        _applier.ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>())
            .Returns<GroupingApplyResult>(_ => throw new SkillVerificationException(GroupingSkillNames.Apply, "rolled back"));

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("rolled back");
        _snapshotStore.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [Test]
    public async Task SuccessfulApply_DropsTodaysSnapshot()
    {
        GivenPlan(AddClient());

        await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        _snapshotStore.Received(1).Remove(GroupingFeasibilityDay.KeyFor(DateOnly.FromDateTime(Today)));
    }

    [Test]
    public async Task RestrictedScope_SkipsTheNewGroupAndEverythingThatTargetsIt_WithoutAskingForItsName()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup(), AddClientToNewGroup(), AddClient());
        var skill = NewSkill(TestGroupScopeGuard.Restricted([GroupId], "Pflege"));

        var result = await skill.ExecuteAsync(Ctx(AllGroupRights), Params(apply: true));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(GroupingProposalPermissionFilter.RootGroupOutOfScope);
        result.Message.ShouldContain(GroupingProposalPermissionFilter.NewGroupSkipped);
        await _applier.Received(1).ApplyAsync(
            Arg.Is<GroupingApplyCommand>(command =>
                command.Proposals.Count == 1
                && command.Proposals[0].Kind == GroupingProposalKind.AddClient
                && command.Proposals[0].GroupId == GroupId
                && command.NewGroupName == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void RestrictedScope_NewGroupAndItsDependentsAreSkipped()
    {
        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(CreateGroup(), AddShiftToNewGroup(), AddClientToNewGroup(), AddClient()),
            AllGroupRights,
            GroupScopeAccess.Restricted([GroupId], ["Pflege"]));

        decision.Permitted.ShouldHaveSingleItem().GroupId.ShouldBe(GroupId);
        decision.Skipped.Select(item => item.Reason).ShouldBe(
            [GroupingProposalPermissionFilter.RootGroupOutOfScope, GroupingProposalPermissionFilter.NewGroupSkipped, GroupingProposalPermissionFilter.NewGroupSkipped],
            ignoreOrder: true);
    }

    [Test]
    public void MissingCreateRight_NewGroupAndItsDependentsAreSkipped()
    {
        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(CreateGroup(), AddShiftToNewGroup(), AddClientToNewGroup(), AddClient()),
            [Permissions.CanEditClients, Permissions.CanViewGroups, Permissions.CanEditShifts],
            GroupScopeAccess.Unrestricted());

        decision.Permitted.ShouldHaveSingleItem().GroupId.ShouldBe(GroupId);
        decision.Skipped.Select(item => item.Reason).ShouldBe(
            [GroupingProposalPermissionFilter.MissingCreateGroupRight, GroupingProposalPermissionFilter.NewGroupSkipped, GroupingProposalPermissionFilter.NewGroupSkipped],
            ignoreOrder: true);
    }

    [Test]
    public void CreateGroupListedLast_StillSkipsItsDependents()
    {
        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(AddShiftToNewGroup(), AddClientToNewGroup(), AddClient(), CreateGroup()),
            [Permissions.CanEditClients, Permissions.CanViewGroups, Permissions.CanEditShifts],
            GroupScopeAccess.Unrestricted());

        decision.Permitted.ShouldHaveSingleItem().GroupId.ShouldBe(GroupId);
        decision.Skipped.Count.ShouldBe(3);
    }

    [Test]
    public void AllowedCreate_KeepsTheNewGroupAndItsDependents()
    {
        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(CreateGroup(), AddShiftToNewGroup(), AddClientToNewGroup()),
            AllGroupRights,
            GroupScopeAccess.Unrestricted());

        decision.Permitted.Count.ShouldBe(3);
        decision.Skipped.ShouldBeEmpty();
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void NoPermittedCreate_MeansNoPermittedChangeOnTheNewGroup(bool restricted, bool mayCreate)
    {
        var permissions = mayCreate ? AllGroupRights : AllGroupRights.Where(p => p != Permissions.CanCreateGroups).ToArray();
        var scope = restricted ? GroupScopeAccess.Restricted([GroupId], ["Pflege"]) : GroupScopeAccess.Unrestricted();

        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(AddClientToNewGroup(), CreateGroup(), AddShiftToNewGroup(), AddClient(), Remove()),
            permissions,
            scope);

        var createPermitted = decision.Permitted.Any(proposal => proposal.Kind == GroupingProposalKind.CreateGroup);
        createPermitted.ShouldBe(!restricted && mayCreate);
        if (!createPermitted)
        {
            decision.Permitted.ShouldNotContain(proposal => proposal.GroupId == null);
        }
        else
        {
            decision.Permitted.ShouldContain(proposal => proposal.Kind != GroupingProposalKind.CreateGroup && proposal.GroupId == null);
        }
    }

    [Test]
    public void SkippedAddition_AlsoSkipsTheRemovalOfTheSameEmployee()
    {
        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(AddClient(groupId: OtherRootGroupId), Remove(), Remove(clientId: SecondClientId)),
            AllGroupRights,
            GroupScopeAccess.Restricted([GroupId], ["Pflege"]));

        decision.Permitted.ShouldHaveSingleItem().ClientId.ShouldBe(SecondClientId);
        decision.Skipped.Select(item => item.Reason).ShouldBe(
            [GroupingProposalPermissionFilter.GroupOutOfScope, GroupingProposalPermissionFilter.AdditionSkipped],
            ignoreOrder: true);
    }

    [Test]
    public void PermittedCreateWithoutAnyPermittedChangeIntoTheNewGroup_IsSkippedSoNoEmptyGroupIsCreated()
    {
        var decision = GroupingProposalPermissionFilter.Partition(
            ReportOf(CreateGroup(), AddShiftToNewGroup(), AddClient()),
            [Permissions.CanCreateGroups, Permissions.CanEditClients, Permissions.CanViewGroups],
            GroupScopeAccess.Unrestricted());

        decision.Permitted.ShouldHaveSingleItem().Kind.ShouldBe(GroupingProposalKind.AddClient);
        decision.Skipped.Select(item => item.Reason).ShouldBe(
            [GroupingProposalPermissionFilter.MissingShiftRight, GroupingProposalPermissionFilter.NoDependentChanges],
            ignoreOrder: true);
    }

    [Test]
    public async Task OnlyTheEmptyNewGroupWouldRemain_NothingIsAppliedAndNoGroupNameIsAsked()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup());

        var result = await _skill.ExecuteAsync(
            Ctx(Permissions.CanCreateGroups, Permissions.CanEditClients, Permissions.CanViewGroups), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(GroupingProposalPermissionFilter.NoDependentChanges);
        result.Message.ShouldNotContain(ApplyGroupingPlanSkill.NewGroupNameParameter);
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
    }

    [Test]
    public void RestrictedScope_ChildGroupWithoutNestedSetRoot_IsInScopeOverItsParent()
    {
        var childId = Guid.NewGuid();
        var report = ReportOf(AddClient(groupId: childId)) with
        {
            GroupLineage = new Dictionary<Guid, IReadOnlyList<Guid>> { [childId] = [childId, GroupId], [GroupId] = [GroupId] },
        };

        var decision = GroupingProposalPermissionFilter.Partition(report, AllGroupRights, GroupScopeAccess.Restricted([GroupId], ["Pflege"]));

        decision.Permitted.ShouldHaveSingleItem().GroupId.ShouldBe(childId);
        decision.Skipped.ShouldBeEmpty();
    }

    [Test]
    public async Task RestrictedScope_SkippedChangesOfForeignGroups_AreCountedButNeverNamed()
    {
        GivenPlan(AddClient(groupId: OtherRootGroupId, clientId: SecondClientId), AddClient());
        var skill = NewSkill(TestGroupScopeGuard.Restricted([GroupId], "Pflege"));

        var result = await skill.ExecuteAsync(Ctx(AllGroupRights), Params());

        result.Success.ShouldBeTrue();
        var data = System.Text.Json.JsonSerializer.Serialize(result.Data);
        data.ShouldNotContain("Extern");
        data.ShouldNotContain("Ben Beispiel");
        result.Message!.ShouldNotContain("Extern");
        result.Message.ShouldNotContain("Ben Beispiel");
        result.Message.ShouldContain("1 further change(s)");
    }

    [Test]
    public void RestrictedScope_AddingAnEmployeeWhoseGroupsAreAllForeign_IsSkippedAsClientOutOfScope()
    {
        var report = ReportOf(AddClient(clientId: SecondClientId)) with { MemberGroups = ForeignSecondClient() };

        var decision = GroupingProposalPermissionFilter.Partition(report, AllGroupRights, GroupScopeAccess.Restricted([GroupId], ["Pflege"]));

        decision.Permitted.ShouldBeEmpty();
        decision.Skipped.ShouldHaveSingleItem().Reason.ShouldBe(GroupingProposalPermissionFilter.ClientOutOfScope);
    }

    [Test]
    public void RestrictedScope_SkippedForeignAddition_AlsoSkipsTheRemovalOfTheSameEmployee()
    {
        var report = ReportOf(AddClient(clientId: SecondClientId), Remove(clientId: SecondClientId)) with { MemberGroups = ForeignSecondClient() };

        var decision = GroupingProposalPermissionFilter.Partition(report, AllGroupRights, GroupScopeAccess.Restricted([GroupId], ["Pflege"]));

        decision.Permitted.ShouldBeEmpty();
        decision.Skipped.Select(item => item.Reason).ShouldBe(
            [GroupingProposalPermissionFilter.ClientOutOfScope, GroupingProposalPermissionFilter.AdditionSkipped],
            ignoreOrder: true);
    }

    [TestCase(true, false, TestName = "EmployeeWithOneGroupInScope")]
    [TestCase(false, false, TestName = "UngroupedEmployee")]
    [TestCase(false, true, TestName = "UnrestrictedCallerAddingAForeignEmployee")]
    public void AddingAnEmployee_WhoIsVisibleToTheCaller_IsPermitted(bool alsoInScope, bool unrestricted)
    {
        var memberGroups = alsoInScope
            ? new Dictionary<Guid, IReadOnlyList<Guid>> { [SecondClientId] = [OtherRootGroupId, GroupId] }
            : unrestricted ? ForeignSecondClient() : new Dictionary<Guid, IReadOnlyList<Guid>>();
        var report = ReportOf(AddClient(clientId: SecondClientId)) with { MemberGroups = memberGroups };
        var scope = unrestricted ? GroupScopeAccess.Unrestricted() : GroupScopeAccess.Restricted([GroupId], ["Pflege"]);

        var decision = GroupingProposalPermissionFilter.Partition(report, AllGroupRights, scope);

        decision.Permitted.ShouldHaveSingleItem().ClientId.ShouldBe(SecondClientId);
        decision.Skipped.ShouldBeEmpty();
    }

    [Test]
    public async Task RestrictedScope_SkippedAdditionOfAForeignEmployee_IsCountedButNeverNamed()
    {
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Plan(call.Arg<GroupingAnalysisRequest>(), [AddClient(clientId: SecondClientId), AddClient()]) with
            {
                MemberGroups = ForeignSecondClient(),
            });
        var skill = NewSkill(TestGroupScopeGuard.Restricted([GroupId], "Pflege"));

        var result = await skill.ExecuteAsync(Ctx(AllGroupRights), Params());

        result.Success.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldNotContain("Ben Beispiel");
        result.Message!.ShouldNotContain("Ben Beispiel");
        result.Message.ShouldContain("Anna Muster");
        result.Message.ShouldContain("1 further change(s)");
    }

    [Test]
    public async Task ApplyWithoutPreviewInThisSession_IsRefusedAndWritesNothing()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();

        var result = await skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(ApplyGroupingPlanSkill.PreviewRequiredMarker);
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        _snapshotStore.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [Test]
    public async Task PreviewThenApply_BySameUser_Applies()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();
        var context = Ctx(Roles.Admin);

        (await skill.ExecuteAsync(context, Params())).Success.ShouldBeTrue();
        var result = await skill.ExecuteAsync(context, Params(apply: true));

        result.Success.ShouldBeTrue();
        await _applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PreviewByAnotherUser_DoesNotUnlockApply()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();

        await skill.ExecuteAsync(Ctx(Roles.Admin), Params());
        var result = await skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(ApplyGroupingPlanSkill.PreviewRequiredMarker);
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
    }

    [Test]
    public async Task SuccessfulApply_ConsumesThePreview()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();
        var context = Ctx(Roles.Admin);

        await skill.ExecuteAsync(context, Params());
        (await skill.ExecuteAsync(context, Params(apply: true))).Success.ShouldBeTrue();
        var second = await skill.ExecuteAsync(context, Params(apply: true));

        second.Success.ShouldBeFalse();
        await _applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyInTheSameChatTurnAsThePreview_IsRefusedAndWritesNothing()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();
        var turn = Ctx(Roles.Admin) with { TurnId = Guid.NewGuid() };

        (await skill.ExecuteAsync(turn, Params())).Success.ShouldBeTrue();
        var result = await skill.ExecuteAsync(turn, Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain(ApplyGroupingPlanSkill.SameTurnMarker);
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        _snapshotStore.DidNotReceiveWithAnyArgs().Remove(default!);
    }

    [Test]
    public async Task ApplyRefusedInThePreviewTurn_SucceedsInTheUsersNextTurn()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();
        var previewTurn = Ctx(Roles.Admin) with { TurnId = Guid.NewGuid() };
        var confirmingTurn = previewTurn with { TurnId = Guid.NewGuid() };

        await skill.ExecuteAsync(previewTurn, Params());
        (await skill.ExecuteAsync(previewTurn, Params(apply: true))).Success.ShouldBeFalse();
        var result = await skill.ExecuteAsync(confirmingTurn, Params(apply: true));

        result.Success.ShouldBeTrue();
        await _applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PreviewRepeatedInTheConfirmingTurn_StillAllowsTheApply()
    {
        GivenPlan(AddClient());
        var skill = SkillWithRealPreviewGate();
        var previewTurn = Ctx(Roles.Admin) with { TurnId = Guid.NewGuid() };
        var confirmingTurn = previewTurn with { TurnId = Guid.NewGuid() };

        await skill.ExecuteAsync(previewTurn, Params());
        await skill.ExecuteAsync(confirmingTurn, Params());
        var result = await skill.ExecuteAsync(confirmingTurn, Params(apply: true));

        result.Success.ShouldBeTrue();
        await _applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_IsRecordedForTheFullRecomputedFingerprint()
    {
        GivenPlan(AddClient());

        await _skill.ExecuteAsync(Ctx(Roles.Admin), Params());

        _previews.Received(1).RecordPreview(Arg.Any<Guid>(), Fingerprint, Arg.Any<Guid?>());
    }

    private static Dictionary<Guid, IReadOnlyList<Guid>> ForeignSecondClient() => new() { [SecondClientId] = [OtherRootGroupId] };

    private static GroupingUnitSummary RemainingGapsInPflege() => new(
        GroupId, 196, 0, 2, 247, 246, GroupingIneligibilityReason.NoActiveContract, 246,
        [new GroupingCapacityGap(DayOfWeek.Monday, 88, 1)]);

    [Test]
    public async Task SuccessfulApply_RecomputesAndStatesTheRemainingGapsPerUnit()
    {
        var before = ReportOf(AddClient());
        var after = ReportOf() with { UnitSummaries = [RemainingGapsInPflege()] };
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>()).Returns(before, after);

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeTrue();
        await _analyzer.Received(2).AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
        result.Message.ShouldContain("\"Pflege\"");
        result.Message.ShouldContain("2 duties no employee in the company can take");
        result.Message.ShouldContain("0 duties nobody in the unit can take");
        result.Message.ShouldContain("Monday (demand 88, available 1)");
        result.Message.ShouldContain("reason=NoActiveContract employees=246/247 unit=\"Pflege\"");
        result.Message.ShouldContain(GroupingReportTexts.RemainingGapsSentence);
        result.Message.ShouldNotContain("now plannable");
    }

    [Test]
    public async Task SuccessfulApply_WithoutRemainingGaps_SaysSoWithoutClaimingPlannability()
    {
        var healthy = new GroupingUnitSummary(GroupId, 5, 0, 0, 5, 0, null, 0, []);
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(ReportOf(AddClient()), ReportOf() with { UnitSummaries = [healthy] });

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotContain(GroupingReportTexts.RemainingGapsSentence);
        result.Message.ShouldContain(GroupingReportTexts.NoRemainingGapsSentence);
    }

    [Test]
    public async Task FailedRecomputeAfterApply_KeepsTheApplySuccessful_AndAsksForANewCheck()
    {
        var calls = 0;
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? ReportOf(AddClient()) : throw new InvalidOperationException("boom"));

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeTrue();
        await _applier.Received(1).ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
        result.Message.ShouldContain(GroupingReportTexts.RecomputeFailedSentence);
    }

    [Test]
    public async Task Preview_DoesNotRecompute()
    {
        GivenPlan(AddClient());

        await _skill.ExecuteAsync(Ctx(Roles.Admin), Params());

        await _analyzer.Received(1).AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
    }
}
