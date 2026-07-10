// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_communication: loads via GetQuery&lt;CommunicationResource&gt;, mutates only
/// supplied fields, dispatches PutCommand&lt;CommunicationResource&gt; and verifies the write by
/// re-reading the entry; a mismatch on re-read yields an honest error, an unknown id yields an error.
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

    private static CommunicationResource Communication(Guid id, string value = "old@example.com") => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Type = CommunicationTypeEnum.PrivateMail,
        Value = value,
        Description = "Private",
        Prefix = string.Empty
    };

    [Test]
    public async Task UpdateValue_DispatchesPutCommand_AndVerifiesByReread()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(Communication(id), Communication(id, "new@example.com"));
        mediator.Send(Arg.Any<PutCommand<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<CommunicationResource>)ci[0]).Resource);
        var skill = new UpdateCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = id.ToString(),
            ["value"] = "new@example.com"
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<CommunicationResource>>(c =>
                c.Resource.Id == id &&
                c.Resource.Value == "new@example.com"),
            Arg.Any<CancellationToken>());
        await mediator.Received(2).Send(Arg.Any<GetQuery<CommunicationResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateValue_RereadStillHoldsOldValue_ReturnsVerificationError()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(Communication(id), Communication(id, "old@example.com"));
        mediator.Send(Arg.Any<PutCommand<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<CommunicationResource>)ci[0]).Resource);
        var skill = new UpdateCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = id.ToString(),
            ["value"] = "new@example.com"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
        result.Message.ShouldContain("value");
    }

    [Test]
    public async Task UpdateValue_RereadThrowsAfterPut_ReturnsVerificationError()
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? Communication(id)
                : throw new KeyNotFoundException());
        mediator.Send(Arg.Any<PutCommand<CommunicationResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<CommunicationResource>)ci[0]).Resource);
        var skill = new UpdateCommunicationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["communicationId"] = id.ToString(),
            ["value"] = "new@example.com"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
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
