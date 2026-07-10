// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ListContainerTemplateSkill: a container's weekday templates are listed read-only via
/// GetContainerTemplatesQuery, and a shift that is not a container (ShiftType.IsContainer) is rejected.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListContainerTemplateSkillTests
{
    private IShiftRepository _shiftRepository = null!;
    private IMediator _mediator = null!;
    private ListContainerTemplateSkill _skill = null!;

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid TaskShiftId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _mediator = Substitute.For<IMediator>();
        _skill = new ListContainerTemplateSkill(_shiftRepository, _mediator);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task ListsTemplate_ForRequestedWeekday()
    {
        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, ShiftType = ShiftType.IsContainer });

        var template = new ContainerTemplateResource
        {
            Id = Guid.NewGuid(),
            ContainerId = ContainerId,
            Weekday = 2,
            IsHoliday = false,
            IsWeekdayAndHoliday = false,
            FromTime = new TimeOnly(6, 0),
            UntilTime = new TimeOnly(22, 0),
            ContainerTemplateItems = new List<ContainerTemplateItemResource>
            {
                new()
                {
                    ShiftId = TaskShiftId,
                    Shift = new ShiftResource { Id = TaskShiftId, Name = "Task A" },
                    StartItem = new TimeOnly(6, 0),
                    EndItem = new TimeOnly(14, 0)
                }
            }
        };

        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ContainerTemplateResource> { template });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["containerId"] = ContainerId.ToString(),
            ["weekday"] = 2
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetArrayLength().ShouldBe(1);
        data[0].GetProperty("Weekday").GetInt32().ShouldBe(2);
        data[0].GetProperty("Items")[0].GetProperty("Name").GetString().ShouldBe("Task A");
        await _mediator.Received(1).Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenShiftIsNotAContainer()
    {
        _shiftRepository.Get(ContainerId).Returns(new Shift { Id = ContainerId, ShiftType = ShiftType.IsTask });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["containerId"] = ContainerId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not a container shift");
        await _mediator.DidNotReceive().Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>());
    }
}
