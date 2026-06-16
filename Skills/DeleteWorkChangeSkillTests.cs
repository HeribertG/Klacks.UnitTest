// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteWorkChangeSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task DeleteWorkChange_DispatchesDeleteCommand_AndReportsSuccess()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkChangeResource { Id = id, WorkId = Guid.NewGuid(), Type = WorkChangeType.CorrectionEnd });
        var skill = new DeleteWorkChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<WorkChangeResource>>(c => c.Id == id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns((WorkChangeResource?)null);
        var skill = new DeleteWorkChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["workChangeId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }
}
