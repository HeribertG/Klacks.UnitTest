// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_membership: dispatches DeleteCommand&lt;MembershipResource&gt; for the given id
/// and reports success; a null result (unknown membership) yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteMembershipSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task DeleteMembership_DispatchesDeleteCommand_AndReportsSuccess()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(new MembershipResource { Id = id, ClientId = Guid.NewGuid() });
        var skill = new DeleteMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<MembershipResource>>(c => c.Id == id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteMembership_UnknownId_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns((MembershipResource?)null);
        var skill = new DeleteMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }
}
