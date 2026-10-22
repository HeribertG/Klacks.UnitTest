// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SealOpenOrdersSkill: groupName and autoAssignGroups are refused together because the
/// assignment works on orders without any group and would therefore write outside the requested group;
/// a caller without CanEditShifts (the planner floor) is refused before anything is read, even when the
/// group scope looks unrestricted; an administrator with unrestricted scope gets full access, while a
/// supervisor (or an administrator with a restricted scope) gets a scope of exactly the visible group
/// subtrees, cannot name a hidden group, cannot run the group assignment and carries that very scope on the
/// preview, the inline apply and the enqueued background job; a groupName is resolved with the central Strict
/// policy (exact name, whole words, label-decorated name), so neither a hidden "Region South" nor a misspelling is
/// ever replaced by the visible "Region North" - both get the "Did the user mean ...?" ask-back and nothing is
/// sealed - and the resolved group is named in the preview; the default call is a preview that tells the user sealing
/// cannot be undone, and apply=true either seals inline (sealable count at or below the synchronous
/// limit) or is handed to the background job queue instead (above it) using the very same preview count.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SealOpenOrdersSkillTests
{
    private const int SynchronousLimit = 25;

    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IGroupRepository _groupRepository = null!;
    private IGroupScopeGuard _groupScopeGuard = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;
    private ISealOpenOrdersJobQueue _jobQueue = null!;
    private SealOpenOrdersSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupScopeGuard = Substitute.For<IGroupScopeGuard>();
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _groupRepository.List().Returns(new List<Group>());
        _jobQueue = Substitute.For<ISealOpenOrdersJobQueue>();
        _jobQueue.Enqueue(Arg.Any<SealOpenOrdersJob>()).Returns(true);
        _mediator.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SealOpenOrdersResult(
                Applied: false, TotalOrders: 3, SealableCount: 1, SealedCount: 0, BlockedCount: 2,
                FailedCount: 0, BlockedOnlyByMissingGroupCount: 2, AutoAssignedCount: 0,
                AutoAssignRequested: false, SealedSample: [], BlockedSample: [], Failures: []));

        _skill = new SealOpenOrdersSkill(_groupRepository, _groupScopeGuard, _mediator, _companyClock, _jobQueue);
    }

    private static SealOpenOrdersResult PreviewWithSealableCount(int sealableCount) => new(
        Applied: false, TotalOrders: sealableCount, SealableCount: sealableCount, SealedCount: 0,
        BlockedCount: 0, FailedCount: 0, BlockedOnlyByMissingGroupCount: 0, AutoAssignedCount: 0,
        AutoAssignRequested: false, SealedSample: [], BlockedSample: [], Failures: []);

    private static SkillExecutionContext Ctx() => CtxFor(Roles.Admin);

    private static SkillExecutionContext SupervisorCtx() => CtxFor(Roles.Authorised);

    private static SkillExecutionContext PlannerCtx() => CtxFor();

    private static SkillExecutionContext CtxFor(params string[] roles) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = Permissions.ExpandRoles(roles)
    };

    private static Group RootGroup(string name) => new() { Id = Guid.NewGuid(), Name = name };

    private static Group ChildGroup(string name, Group root) =>
        new() { Id = Guid.NewGuid(), Name = name, Parent = root.Id, Root = root.Id };

    private (Group VisibleRoot, Group VisibleChild, Group HiddenRoot) SupervisorSeesOnlyAlpha()
    {
        var visibleRoot = RootGroup("Alpha");
        var visibleChild = ChildGroup("Alpha Lakeside", visibleRoot);
        var hiddenRoot = RootGroup("Bravo");
        _groupRepository.List().Returns(new List<Group> { visibleRoot, visibleChild, hiddenRoot });
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([visibleRoot.Id], [visibleRoot.Name]));
        return (visibleRoot, visibleChild, hiddenRoot);
    }

    [Test]
    public async Task Planner_IsRefused_EvenWhenTheGroupScopeLooksUnrestricted()
    {
        Permissions.PlannerFloor.ShouldNotContain(Permissions.CanEditShifts);

        var result = await _skill.ExecuteAsync(PlannerCtx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("right to edit shifts");
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
        _jobQueue.DidNotReceive().Enqueue(Arg.Any<SealOpenOrdersJob>());
    }

    [Test]
    public async Task Admin_WithUnrestrictedScope_GetsFullAccess()
    {
        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => c.Scope.HasFullAccess), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_WithUnrestrictedLookingScope_NeverGetsFullAccess()
    {
        var group = RootGroup("Only Group");
        _groupRepository.List().Returns(new List<Group> { group });

        await _skill.ExecuteAsync(SupervisorCtx(), new Dictionary<string, object>());

        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => !c.Scope.HasFullAccess), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_IsScopedToTheVisibleGroupSubtrees()
    {
        var (visibleRoot, visibleChild, hiddenRoot) = SupervisorSeesOnlyAlpha();

        var result = await _skill.ExecuteAsync(SupervisorCtx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("orders without a group can only be sealed by an administrator");
        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => !c.Scope.HasFullAccess
                && c.Scope.VisibleGroupIds.SetEquals(new[] { visibleRoot.Id, visibleChild.Id })
                && !c.Scope.VisibleGroupIds.Contains(hiddenRoot.Id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdminWithRestrictedScope_IsScopedLikeASupervisor()
    {
        var (visibleRoot, visibleChild, _) = SupervisorSeesOnlyAlpha();

        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => !c.Scope.HasFullAccess
                && c.Scope.VisibleGroupIds.SetEquals(new[] { visibleRoot.Id, visibleChild.Id })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_HiddenGroupName_IsAnsweredExactlyLikeAMissingGroup()
    {
        var (_, _, hiddenRoot) = SupervisorSeesOnlyAlpha();
        const string missingName = "Kilo";

        var hidden = await _skill.ExecuteAsync(
            SupervisorCtx(), new Dictionary<string, object> { ["groupName"] = hiddenRoot.Name });
        var missing = await _skill.ExecuteAsync(
            SupervisorCtx(), new Dictionary<string, object> { ["groupName"] = missingName });

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe(missing.Message.Replace(missingName, hiddenRoot.Name));
        hidden.Message.ShouldContain("Available groups: Alpha, Alpha Lakeside.");
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
    }

    private void SupervisorSeesRegionNorthButNotRegionSouth(bool regionSouthExists)
    {
        var regionNorth = RootGroup("Region North");
        var northCity = ChildGroup("North City", regionNorth);
        var groups = new List<Group> { regionNorth, northCity };
        if (regionSouthExists)
        {
            groups.Add(RootGroup("Region South"));
        }

        _groupRepository.List().Returns(groups);
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([regionNorth.Id], [regionNorth.Name]));
    }

    [Test]
    public async Task Supervisor_HiddenSimilarlyNamedGroup_IsNeverReplacedByTheVisibleOne()
    {
        SupervisorSeesRegionNorthButNotRegionSouth(regionSouthExists: true);
        var parameters = new Dictionary<string, object> { ["groupName"] = "Region South", ["apply"] = true };

        var hidden = await _skill.ExecuteAsync(SupervisorCtx(), parameters);

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe(NameResolutionMessages.SuggestedName("Region North"));
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
        _jobQueue.DidNotReceive().Enqueue(Arg.Any<SealOpenOrdersJob>());

        SupervisorSeesRegionNorthButNotRegionSouth(regionSouthExists: false);
        var missing = await _skill.ExecuteAsync(SupervisorCtx(), parameters);

        hidden.Message.ShouldBe(missing.Message);
    }

    [Test]
    public async Task Admin_MisspelledGroupName_IsRefusedInsteadOfFuzzyMatched()
    {
        var regionNorth = RootGroup("Region North");
        _groupRepository.List().Returns(new List<Group> { regionNorth, RootGroup("Region South") });

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["groupName"] = "Regoin North", ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(NameResolutionMessages.SuggestedName("Region North"));
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_HiddenGroupWithALabelGluedToAVisibleName_IsAnsweredLikeAMissingOne()
    {
        var leitung = RootGroup("Leitung");
        var hidden = RootGroup("Gruppenleitung");
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([leitung.Id], [leitung.Name]));
        var parameters = new Dictionary<string, object> { ["groupName"] = "Gruppenleitung", ["apply"] = true };

        _groupRepository.List().Returns(new List<Group> { leitung, hidden });
        var whenHidden = await _skill.ExecuteAsync(SupervisorCtx(), parameters);
        _groupRepository.List().Returns(new List<Group> { leitung });
        var whenMissing = await _skill.ExecuteAsync(SupervisorCtx(), parameters);

        whenHidden.Success.ShouldBeFalse();
        whenHidden.Message.ShouldBe(whenMissing.Message);
        whenHidden.Message.ShouldBe(NameResolutionMessages.SuggestedName("Leitung"));
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
        _jobQueue.DidNotReceive().Enqueue(Arg.Any<SealOpenOrdersJob>());
    }

    [Test]
    public async Task Supervisor_GroupNameEndingInsideAWord_IsAskedBack_AndNothingIsSealed()
    {
        SupervisorSeesRegionNorthButNotRegionSouth(regionSouthExists: true);

        var result = await _skill.ExecuteAsync(
            SupervisorCtx(), new Dictionary<string, object> { ["groupName"] = "Regio", ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(NameResolutionMessages.SuggestedName("Region North"));
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
        _jobQueue.DidNotReceive().Enqueue(Arg.Any<SealOpenOrdersJob>());
    }

    [TestCase("Region North")]
    [TestCase("region north")]
    [TestCase("Gruppe Region North")]
    [TestCase("Region-North")]
    public async Task Supervisor_ExactGroupName_IsResolved_AndNamedInThePreview(string groupName)
    {
        SupervisorSeesRegionNorthButNotRegionSouth(regionSouthExists: true);

        var result = await _skill.ExecuteAsync(
            SupervisorCtx(), new Dictionary<string, object> { ["groupName"] = groupName });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Group: 'Region North'.");
        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => c.GroupId.HasValue), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_VisibleGroupName_IsResolved()
    {
        var (_, visibleChild, _) = SupervisorSeesOnlyAlpha();

        var result = await _skill.ExecuteAsync(
            SupervisorCtx(), new Dictionary<string, object> { ["groupName"] = visibleChild.Name });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => c.GroupId == visibleChild.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_AutoAssignGroups_IsRefused()
    {
        SupervisorSeesOnlyAlpha();

        var result = await _skill.ExecuteAsync(
            SupervisorCtx(), new Dictionary<string, object> { ["autoAssignGroups"] = true, ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("autoAssignGroups is only available to administrators");
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_WithoutAnyVisibleGroup_IsRefused()
    {
        _groupRepository.List().Returns(new List<Group> { RootGroup("Bravo") });
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([], []));

        var result = await _skill.ExecuteAsync(SupervisorCtx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No group is assigned to your group scope");
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Supervisor_InlineApply_SendsPreviewAndApplyWithTheIdenticalScope()
    {
        SupervisorSeesOnlyAlpha();
        var commands = new List<SealOpenOrdersCommand>();
        _mediator.When(m => m.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>()))
            .Do(ci => commands.Add(ci.Arg<SealOpenOrdersCommand>()));

        await _skill.ExecuteAsync(SupervisorCtx(), new Dictionary<string, object> { ["apply"] = true });

        commands.Count.ShouldBe(2);
        commands[0].Apply.ShouldBeFalse();
        commands[1].Apply.ShouldBeTrue();
        commands[1].Scope.ShouldBeSameAs(commands[0].Scope);
        commands[1].ShouldBe(commands[0] with { Apply = true });
    }

    [Test]
    public async Task Supervisor_AboveTheSynchronousLimit_EnqueuesAJobCarryingTheScopedCommand()
    {
        var (visibleRoot, visibleChild, _) = SupervisorSeesOnlyAlpha();
        _mediator.Send(Arg.Is<SealOpenOrdersCommand>(c => !c.Apply), Arg.Any<CancellationToken>())
            .Returns(PreviewWithSealableCount(SynchronousLimit + 1));
        SealOpenOrdersJob? enqueued = null;
        _jobQueue.Enqueue(Arg.Do<SealOpenOrdersJob>(j => enqueued = j)).Returns(true);
        var ctx = SupervisorCtx();

        var result = await _skill.ExecuteAsync(ctx, new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeTrue();
        enqueued.ShouldNotBeNull();
        enqueued.UserId.ShouldBe(ctx.UserId);
        enqueued.Command.Apply.ShouldBeTrue();
        enqueued.Command.Scope.HasFullAccess.ShouldBeFalse();
        enqueued.Command.Scope.VisibleGroupIds.ShouldBe(
            new[] { visibleRoot.Id, visibleChild.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task RejectsGroupNameCombinedWithAutoAssignGroups()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["autoAssignGroups"] = true
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("cannot be combined");
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DefaultCall_IsAPreviewThatWarnsSealingCannotBeUndone()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Preview");
        result.Message.ShouldContain("cannot be undone");
        result.Message.ShouldContain("autoAssignGroups=true");
        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => !c.Apply && !c.AutoAssignGroups), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RejectsANonPositiveMaxCount()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["maxCount"] = 0 });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("maxCount");
    }

    [Test]
    public async Task ApplyAtOrBelowTheSynchronousLimit_SealsInline_UsingThePreviewCountToDecide()
    {
        // Default mock returns SealableCount=1 for every command, well below the limit.
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Sealed");
        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => c.Apply), Arg.Any<CancellationToken>());
        _jobQueue.DidNotReceive().Enqueue(Arg.Any<SealOpenOrdersJob>());
    }

    [Test]
    public async Task ApplyAboveTheSynchronousLimit_IsEnqueuedAsABackgroundJob_InsteadOfSealingInline()
    {
        _mediator.Send(Arg.Is<SealOpenOrdersCommand>(c => !c.Apply), Arg.Any<CancellationToken>())
            .Returns(PreviewWithSealableCount(SynchronousLimit + 1));
        var ctx = Ctx();

        var result = await _skill.ExecuteAsync(ctx, new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("background job");
        result.Message.ShouldContain("inbox");
        var accepted = result.Data.ShouldBeOfType<SealOpenOrdersJobAcceptedResult>();
        accepted.PlannedCount.ShouldBe(SynchronousLimit + 1);

        await _mediator.Received(1).Send(
            Arg.Is<SealOpenOrdersCommand>(c => !c.Apply), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(
            Arg.Is<SealOpenOrdersCommand>(c => c.Apply), Arg.Any<CancellationToken>());
        _jobQueue.Received(1).Enqueue(Arg.Is<SealOpenOrdersJob>(
            j => j.JobId == accepted.JobId && j.UserId == ctx.UserId && j.Command.Apply));
    }

    [Test]
    public async Task ApplyAboveTheSynchronousLimit_QueueFull_ReturnsAnErrorInsteadOfAFakeJobId()
    {
        _mediator.Send(Arg.Is<SealOpenOrdersCommand>(c => !c.Apply), Arg.Any<CancellationToken>())
            .Returns(PreviewWithSealableCount(SynchronousLimit + 1));
        _jobQueue.Enqueue(Arg.Any<SealOpenOrdersJob>()).Returns(false);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("queue is currently full");
        await _mediator.DidNotReceive().Send(
            Arg.Is<SealOpenOrdersCommand>(c => c.Apply), Arg.Any<CancellationToken>());
    }
}
