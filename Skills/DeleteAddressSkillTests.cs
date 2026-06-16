// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_address: dispatches DeleteCommand&lt;AddressResource&gt; for the given id and
/// reports success; a null result (unknown address) yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
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
    public async Task DeleteAddress_DispatchesDeleteCommand_AndReportsSuccess()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AddressResource { Id = id });
        var skill = new DeleteAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<AddressResource>>(c => c.Id == id),
            Arg.Any<CancellationToken>());
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
    }
}
