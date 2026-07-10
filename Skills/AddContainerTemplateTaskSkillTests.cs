// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddContainerTemplateTaskSkill: an eligible task shift is added to a weekday template
/// under the container edit lock and the write is verified from the mutation's own return value, and a
/// container that is already locked by another user is rejected before any mutation is sent or released.
/// </summary>

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddContainerTemplateTaskSkillTests
{
    private const int StorageWeekday = 2;
    private const int IsoWeekday = 2;

    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private IUserService _userService = null!;
    private IContainerAvailableTasksService _availableTasksService = null!;
    private AddContainerTemplateTaskSkill _skill = null!;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid TaskShiftId = Guid.NewGuid();
    private static readonly Guid LockId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mediator = Substitute.For<IMediator>();
        _userService = Substitute.For<IUserService>();
        _availableTasksService = Substitute.For<IContainerAvailableTasksService>();
        _skill = new AddContainerTemplateTaskSkill(_shiftRepository, _mediator, _userService, _availableTasksService);

        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, ShiftType = ShiftType.IsContainer });

        var taskShift = new Shift
        {
            Id = TaskShiftId,
            ShiftType = ShiftType.IsTask,
            Abbreviation = "T1",
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            BriefingTime = new TimeOnly(0, 5),
            DebriefingTime = new TimeOnly(0, 5),
            TravelTimeBefore = new TimeOnly(0, 10),
            TravelTimeAfter = new TimeOnly(0, 10)
        };
        _shiftRepository.Get(TaskShiftId).Returns(taskShift);

        var template = new ContainerTemplateResource
        {
            Id = Guid.NewGuid(),
            ContainerId = ContainerId,
            Weekday = StorageWeekday,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerTemplateItems = new List<ContainerTemplateItemResource>()
        };
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource> { template });

        _availableTasksService.GetAvailableTasksAsync(
                ContainerId,
                StorageWeekday,
                template.FromTime,
                template.UntilTime,
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<IReadOnlyCollection<Guid>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Shift> { taskShift });

        _userService.GetInstanceId().Returns("instance-1");

        var updatedTemplate = new ContainerTemplateResource
        {
            Id = template.Id,
            ContainerId = ContainerId,
            Weekday = StorageWeekday,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = template.FromTime,
            UntilTime = template.UntilTime,
            ContainerTemplateItems = new List<ContainerTemplateItemResource>
            {
                new() { ShiftId = TaskShiftId }
            }
        };
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource> { updatedTemplate });

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

    [Test]
    public async Task AddsTask_AndReportsVerified_WhenLockAcquiredAndTaskEligible()
    {
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = true });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<PutContainerTemplatesCommand>(c =>
                c.ContainerId == ContainerId
                && c.Resources.Any(r => r.ContainerTemplateItems.Any(i => i.ShiftId == TaskShiftId))),
            Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<ReleaseContainerLockCommand>(c => c.LockId == LockId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_AndSendsNothingElse_WhenLockCannotBeAcquired()
    {
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = LockId, Acquired = false });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("currently being edited");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>());
    }
}
