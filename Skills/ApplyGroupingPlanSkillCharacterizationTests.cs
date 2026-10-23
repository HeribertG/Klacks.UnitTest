// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for ApplyGroupingPlanSkill.ExecuteAsync, written before its long body was split into
/// private steps. They complement ApplyGroupingPlanSkillTests with the exact texts (missing fingerprint, stale plan,
/// nothing to change, preview gate, nothing permitted, preview with skipped changes and the group-name question,
/// missing group name, unreadable start date, applied summary), the leading part of the serialized result data, that
/// the preview gate is checked before the permission partition, that a padded new group name reaches the applier
/// trimmed (preview and apply), and the call order preview-start -> record preview, respectively
/// apply -> drop snapshot -> forget preview -> recompute.
/// </summary>

using System.Text.Json;
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
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ApplyGroupingPlanSkillCharacterizationTests
{
    private const string StartDateSentence = " Membership start date: 2026-10-01.";
    private const string ConfirmSentence = " Ask the user to confirm, then call again with apply=true and the same fingerprint.";

    private static readonly DateTime Today = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
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
        _skill = new ApplyGroupingPlanSkill(
            _analyzer, _applier, Substitute.For<IGroupRepository>(), TestGroupScopeGuard.Unrestricted(), _clock, _snapshotStore,
            _previews, NullLogger<ApplyGroupingPlanSkill>.Instance);
    }

    private static GroupingFeasibilityReport Plan(GroupingAnalysisRequest request, IReadOnlyList<GroupingProposal> proposals) =>
        new(request, [], proposals, Fingerprint, "r",
            new Dictionary<Guid, string> { [GroupId] = "Pflege" },
            new Dictionary<Guid, IReadOnlyList<Guid>> { [GroupId] = [GroupId] },
            new Dictionary<Guid, IReadOnlyList<Guid>>(),
            new Dictionary<Guid, string> { [ClientId] = "Anna Muster" },
            new Dictionary<Guid, string> { [ShiftId] = "Frueh" }, 1, 1);

    private void GivenPlan(params GroupingProposal[] proposals) =>
        _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Plan(call.Arg<GroupingAnalysisRequest>(), proposals));

    private static GroupingProposal AddClient() =>
        new(GroupingProposalKind.AddClient, GroupId, null, ClientId, null, GroupingFindingCode.ShiftUncoveredInGroup);

    private static GroupingProposal AddShift() =>
        new(GroupingProposalKind.AddShift, GroupId, null, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup);

    private static GroupingProposal CreateGroup() =>
        new(GroupingProposalKind.CreateGroup, null, GroupingFeasibilityDefaults.NewGroupKey, null, null, GroupingFindingCode.ShiftWithoutGroup);

    private static GroupingProposal AddShiftToNewGroup() =>
        new(GroupingProposalKind.AddShift, null, GroupingFeasibilityDefaults.NewGroupKey, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup);

    private static SkillExecutionContext Ctx(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = permissions.ToList(),
        TurnId = Guid.NewGuid()
    };

    private static Dictionary<string, object> Params(bool apply = false, string? code = null) => new()
    {
        [ApplyGroupingPlanSkill.FingerprintParameter] = code ?? Fingerprint[..GroupingFeasibilityDefaults.FingerprintDisplayLength],
        [ApplyGroupingPlanSkill.ApplyParameter] = apply,
        [ApplyGroupingPlanSkill.ValidFromParameter] = "2026-10-01",
    };

    [Test]
    public async Task MissingFingerprint_IsRefused_WithoutAnalysis()
    {
        var parameters = Params();
        parameters.Remove(ApplyGroupingPlanSkill.FingerprintParameter);

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "The plan code from the group plannability check is required. Run the check first and use the code it reports.");
        await _analyzer.DidNotReceiveWithAnyArgs().AnalyzeAsync(default!, default);
    }

    [Test]
    public async Task StalePlanCode_NamesTheCurrentCode()
    {
        GivenPlan(AddClient());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true, code: new string('c', 12)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "The data changed since the check, so this plan code is no longer valid (the current plan has the code "
            + GroupingFingerprint.Shorten(Fingerprint)
            + "). Nothing was changed. Show the user the new check result before applying anything.");
    }

    [Test]
    public async Task EmptyPlan_ReportsNothingToChange()
    {
        GivenPlan();

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("The current plan contains no group changes. Nothing was changed.");
        JsonSerializer.Serialize(result.Data).ShouldBe("{\"Applied\":false}");
        _previews.DidNotReceiveWithAnyArgs().GetPreviewStatus(default, default!, default);
    }

    [Test]
    public async Task ApplyWithoutPreview_IsRefusedBeforeThePermissionCheck()
    {
        GivenPlan(AddClient());
        _previews.GetPreviewStatus(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>()).Returns(GroupingPreviewStatus.Missing);

        var result = await _skill.ExecuteAsync(Ctx(), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Not applied (preview required): this user has not previewed this plan in the last 30 minutes. "
            + "Call apply_grouping_plan with apply=false and the same fingerprint first, show the user the listed changes "
            + "and ask for confirmation; only after the user confirms that preview call again with apply=true. Nothing was changed.");
    }

    [Test]
    public async Task ApplyInThePreviewTurn_IsRefused()
    {
        GivenPlan(AddClient());
        _previews.GetPreviewStatus(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid?>()).Returns(GroupingPreviewStatus.SameTurn);

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Not applied (user confirmation required): the preview of this plan was shown in this same turn, so the user has not "
            + "confirmed it yet. Show the user the listed changes and ask the user to confirm first; call again with apply=true "
            + "and the same fingerprint only after the user has answered. Nothing was changed.");
    }

    [Test]
    public async Task NoPermittedChange_ListsTheReasons()
    {
        GivenPlan(AddClient());

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"None of the proposed changes may be carried out by this user: {GroupingProposalPermissionFilter.MissingClientRight}. Nothing was changed.");
        await _applier.DidNotReceiveWithAnyArgs().PreviewStartAsync(default!, default);
        _previews.DidNotReceiveWithAnyArgs().RecordPreview(default, default!, default);
    }

    [Test]
    public async Task Preview_WithASkippedChange_HasTheExactText_AndRecordsThePreviewAfterTheStartPreview()
    {
        GivenPlan(AddShift(), AddClient());
        var context = Ctx(Permissions.CanEditClients, Permissions.CanViewGroups);

        var result = await _skill.ExecuteAsync(context, Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Preview: 1 change(s) would be made in this order: 1. "
            + GroupingReportTexts.Actions[GroupingProposalKind.AddClient] + ": Pflege Anna Muster."
            + " 1 change(s) are skipped: " + GroupingReportTexts.Actions[GroupingProposalKind.AddShift]
            + " (" + GroupingProposalPermissionFilter.MissingShiftRight + ")."
            + " Nothing was changed yet." + StartDateSentence + ConfirmSentence);
        var data = JsonSerializer.Serialize(result.Data);
        data.ShouldStartWith("{\"Applied\":false,\"Changes\":[{\"Step\":1,");
        data.ShouldContain("],\"Skipped\":[{\"Change\":{\"Step\":0,");
        Received.InOrder(() =>
        {
            _applier.PreviewStartAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
            _previews.RecordPreview(context.UserId, Fingerprint, context.TurnId);
        });
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
    }

    [Test]
    public async Task Preview_OfANewGroupWithoutName_AsksForTheName()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldStartWith("Preview: 2 change(s) would be made in this order: 1. ");
        result.Message.ShouldEndWith(
            " Nothing was changed yet." + StartDateSentence
            + " Ask the user for the name of the new group (newGroupName)." + ConfirmSentence);
    }

    [Test]
    public async Task Apply_OfANewGroupWithoutName_IsRefused()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup());

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), Params(apply: true));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "The plan creates a new group. Ask the user for its name and call again with newGroupName. Nothing was changed.");
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        await _applier.DidNotReceiveWithAnyArgs().PreviewStartAsync(default!, default);
    }

    [Test]
    public async Task Apply_HasTheExactSummary_AndDropsSnapshotForgetsPreviewThenRecomputes()
    {
        GivenPlan(AddClient());
        var context = Ctx(Roles.Admin);

        var result = await _skill.ExecuteAsync(context, Params(apply: true));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldStartWith(
            "Created 0 group(s), added 0 duty/duties and 1 employee(s) to groups, removed 0 employee(s) from groups; "
            + "all confirmed in the database (verified). 0 change(s) were already in place." + StartDateSentence);
        var data = JsonSerializer.Serialize(result.Data);
        data.ShouldStartWith("{\"Applied\":true,\"Result\":{");
        data.ShouldContain("},\"Skipped\":[],\"Remaining\":");
        Received.InOrder(() =>
        {
            _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
            _applier.ApplyAsync(Arg.Any<GroupingApplyCommand>(), Arg.Any<CancellationToken>());
            _snapshotStore.Remove(GroupingFeasibilityDay.KeyFor(DateOnly.FromDateTime(Today)));
            _previews.Forget(context.UserId, Fingerprint);
            _analyzer.AnalyzeAsync(Arg.Any<GroupingAnalysisRequest>(), Arg.Any<CancellationToken>());
        });
        _previews.DidNotReceiveWithAnyArgs().RecordPreview(default, default!, default);
    }

    [Test]
    public async Task Apply_WithAnUnreadableStartDate_IsRefused_AndAppliesNothing()
    {
        GivenPlan(AddClient());
        var parameters = Params(apply: true);
        parameters[ApplyGroupingPlanSkill.ValidFromParameter] = "irgendwann";

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor(ApplyGroupingPlanSkill.ValidFromParameter, "irgendwann"));
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
        await _applier.DidNotReceiveWithAnyArgs().PreviewStartAsync(default!, default);
        _snapshotStore.DidNotReceiveWithAnyArgs().Remove(default!);
        _previews.DidNotReceiveWithAnyArgs().Forget(default, default!);
        _previews.DidNotReceiveWithAnyArgs().RecordPreview(default, default!, default);
    }

    [Test]
    public async Task Apply_OfANewGroup_PassesTheTrimmedGroupNameToTheApplier()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup());
        var parameters = Params(apply: true);
        parameters[ApplyGroupingPlanSkill.NewGroupNameParameter] = "  Team Nord  ";

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        result.Success.ShouldBeTrue(result.Message);
        await _applier.Received(1).ApplyAsync(
            Arg.Is<GroupingApplyCommand>(command => command.NewGroupName == "Team Nord"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_OfANewGroup_WithAPaddedName_UsesTheTrimmedName_AndDoesNotAskForIt()
    {
        GivenPlan(CreateGroup(), AddShiftToNewGroup());
        var parameters = Params();
        parameters[ApplyGroupingPlanSkill.NewGroupNameParameter] = "  Team Nord  ";

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), parameters);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldEndWith(" Nothing was changed yet." + StartDateSentence + ConfirmSentence);
        result.Message.ShouldNotContain(ApplyGroupingPlanSkill.NewGroupNameParameter);
        await _applier.Received(1).PreviewStartAsync(
            Arg.Is<GroupingApplyCommand>(command => command.NewGroupName == "Team Nord"),
            Arg.Any<CancellationToken>());
        await _applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default);
    }
}
