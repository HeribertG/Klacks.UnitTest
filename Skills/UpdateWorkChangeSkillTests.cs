// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateWorkChangeSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private static WorkChangeResource Change(Guid id) => new()
    {
        Id = id,
        WorkId = Guid.NewGuid(),
        Type = WorkChangeType.CorrectionEnd,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        ChangeTime = 0m,
        Description = "old"
    };

    [Test]
    public async Task UpdateChangeTimeAndDescription_DispatchesPutCommand()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(Change(id));
        mediator.Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<WorkChangeResource>)ci[0]).Resource);
        var skill = new UpdateWorkChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = id.ToString(),
            ["changeTime"] = 2.5m,
            ["description"] = "corrected"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<WorkChangeResource>>(c =>
                c.Resource.Id == id &&
                c.Resource.ChangeTime == 2.5m &&
                c.Resource.Description == "corrected"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownWorkChange_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns<WorkChangeResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateWorkChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = Guid.NewGuid().ToString(),
            ["changeTime"] = 2.5m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }
}
