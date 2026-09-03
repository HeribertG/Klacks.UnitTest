// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SealOpenOrdersSkill: groupName and autoAssignGroups are refused together because the
/// assignment works on orders without any group and would therefore write outside the requested group,
/// a caller whose group scope is restricted is rejected before the handler is ever reached because the
/// run seals across the whole installation, the default call is a preview that tells the user sealing
/// cannot be undone, and apply=true either seals inline (sealable count at or below the synchronous
/// limit) or is handed to the background job queue instead (above it) using the very same preview count.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Skills;
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

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task RejectsACallerWithARestrictedGroupScope()
    {
        _groupScopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([Guid.NewGuid()], ["Region Bern"]));

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("unrestricted group scope");
        result.Message.ShouldContain("Region Bern");
        await _mediator.DidNotReceive().Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>());
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
