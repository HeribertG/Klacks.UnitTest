// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_communication: dispatches DeleteCommand&lt;CommunicationResource&gt; for the
/// given id and reports success; a null result (unknown entry) yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteCommunicationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task DeleteCommunication_DispatchesDeleteCommand_AndReportsSuccess()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(new CommunicationResource { Id = id });
        var skill = new DeleteCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<CommunicationResource>>(c => c.Id == id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteCommunication_UnknownId_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns((CommunicationResource?)null);
        var skill = new DeleteCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }
}
