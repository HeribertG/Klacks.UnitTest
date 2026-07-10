// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RemoveContainerTemplateTaskSkill: a present task item is removed from a weekday
/// template under the container edit lock and the removal is verified from the mutation's own return
/// value, and a task that is not present in the template is rejected before the lock is ever acquired.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class RemoveContainerTemplateTaskSkillTests
{
    private const int StorageWeekday = 2;
    private const int IsoWeekday = 2;

    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private IUserService _userService = null!;
    private RemoveContainerTemplateTaskSkill _skill = null!;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid TaskShiftId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mediator = Substitute.For<IMediator>();
        _userService = Substitute.For<IUserService>();
        _skill = new RemoveContainerTemplateTaskSkill(_shiftRepository, _mediator, _userService);

        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, ShiftType = ShiftType.IsContainer });
        _userService.GetInstanceId().Returns("instance-1");

        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["containerId"] = ContainerId.ToString(),
        ["weekday"] = IsoWeekday,
        ["taskShiftId"] = TaskShiftId.ToString()
    };

    private static ContainerTemplateResource TemplateWithItem(bool includeItem)
    {
        var items = includeItem
            ? new List<ContainerTemplateItemResource> { new() { Id = Guid.NewGuid(), ShiftId = TaskShiftId } }
            : new List<ContainerTemplateItemResource>();

        return new ContainerTemplateResource
        {
            Id = Guid.NewGuid(),
            ContainerId = ContainerId,
            Weekday = StorageWeekday,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerTemplateItems = items
        };
    }

    [Test]
    public async Task RemovesTask_AndReportsVerified_WhenItemPresentAndLockAcquired()
    {
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource> { TemplateWithItem(includeItem: true) });
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = true });
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource> { TemplateWithItem(includeItem: false) });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<PutContainerTemplatesCommand>(c =>
                c.ContainerId == ContainerId
                && c.Resources.Any(r => r.ContainerTemplateItems.All(i => i.ShiftId != TaskShiftId))),
            Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_AndNeverAcquiresLock_WhenItemIsNotPresent()
    {
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource> { TemplateWithItem(includeItem: false) });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not present");
        await _mediator.DidNotReceive().Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>());
    }
}
