// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SealOpenOrdersSkill: groupName and autoAssignGroups are refused together because the
/// assignment works on orders without any group and would therefore write outside the requested group,
/// a caller whose group scope is restricted is rejected before the handler is ever reached because the
/// run seals across the whole installation, and the default call is a preview that tells the user sealing
/// cannot be undone.
/// </summary>

using Klacks.Api.Application.Commands.Orders;
using Klacks.Api.Application.DTOs.Orders;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SealOpenOrdersSkillTests
{
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private IGroupRepository _groupRepository = null!;
    private IGroupScopeGuard _groupScopeGuard = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;
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
        _mediator.Send(Arg.Any<SealOpenOrdersCommand>(), Arg.Any<CancellationToken>())
            .Returns(new SealOpenOrdersResult(
                Applied: false, TotalOrders: 3, SealableCount: 1, SealedCount: 0, BlockedCount: 2,
                FailedCount: 0, BlockedOnlyByMissingGroupCount: 2, AutoAssignedCount: 0,
                AutoAssignRequested: false, SealedSample: [], BlockedSample: [], Failures: []));

        _skill = new SealOpenOrdersSkill(_groupRepository, _groupScopeGuard, _mediator, _companyClock);
    }

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
}
