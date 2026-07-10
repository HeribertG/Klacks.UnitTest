// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_address: dispatches DeleteCommand&lt;AddressResource&gt; for the given id and
/// verifies the delete by re-reading the address (which must no longer be readable); a null result
/// (unknown address) or a still-readable address yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteAddressSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task DeleteAddress_ReReadThrowsKeyNotFound_ReportsVerified()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressResource { Id = id });
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns<AddressResource>(_ => throw new KeyNotFoundException());
        var skill = new DeleteAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message!.ShouldContain("verified");
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<AddressResource>>(c => c.Id == id),
            Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAddress_ReReadReturnsNull_ReportsVerified()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressResource { Id = id });
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns((AddressResource?)null);
        var skill = new DeleteAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message!.ShouldContain("verified");
    }

    [Test]
    public async Task DeleteAddress_EntityStillReadable_ReturnsError()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressResource { Id = id });
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressResource { Id = id });
        var skill = new DeleteAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("still readable");
    }

    [Test]
    public async Task DeleteAddress_UnknownId_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns((AddressResource?)null);
        var skill = new DeleteAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>());
    }
}
