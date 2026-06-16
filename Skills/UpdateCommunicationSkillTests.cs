// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_communication: loads via GetQuery&lt;CommunicationResource&gt;, mutates only
/// supplied fields and dispatches PutCommand&lt;CommunicationResource&gt;; an unknown id yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateCommunicationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static CommunicationResource Communication(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Type = CommunicationTypeEnum.PrivateMail,
        Value = "old@example.com",
        Description = "Private",
        Prefix = string.Empty
    };

    [Test]
    public async Task UpdateValue_DispatchesPutCommand_WithMergedValue()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(Communication(id));
        mediator.Send(Arg.Any<PutCommand<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<CommunicationResource>)ci[0]).Resource);
        var skill = new UpdateCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = id.ToString(),
            ["value"] = "new@example.com"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<CommunicationResource>>(c =>
                c.Resource.Id == id &&
                c.Resource.Value == "new@example.com"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns<CommunicationResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = Guid.NewGuid().ToString(),
            ["value"] = "new@example.com"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<CommunicationResource>>(), Arg.Any<CancellationToken>());
    }
}
