// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateMembershipSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static MembershipResource Membership(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Type = 0,
        ValidFrom = new DateTime(2026, 1, 1),
        ValidUntil = null
    };

    [Test]
    public async Task UpdateValidFromAndType_DispatchesPutCommand()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = new UpdateMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validFrom"] = "2026-03-01",
            ["type"] = 1
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c =>
                c.Resource.Id == membershipId &&
                c.Resource.ValidFrom == new DateTime(2026, 3, 1) &&
                c.Resource.Type == 1),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClearValidUntil_RemovesEndDate()
    {
        var membershipId = Guid.NewGuid();
        var membership = Membership(membershipId);
        membership.ValidUntil = new DateTime(2026, 6, 30);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(membership);
        mediator.Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<MembershipResource>)ci[0]).Resource);
        var skill = new UpdateMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["clearValidUntil"] = true
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<MembershipResource>>(c => c.Resource.ValidUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownMembership_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns<MembershipResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = Guid.NewGuid().ToString(),
            ["validFrom"] = "2026-03-01"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidUntilBeforeValidFrom_ReturnsError_NoMutation()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var skill = new UpdateMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString(),
            ["validUntil"] = "2025-12-01"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoFieldsSupplied_ReturnsSuccess_WithoutPut()
    {
        var membershipId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(Membership(membershipId));
        var skill = new UpdateMembershipSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["membershipId"] = membershipId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<MembershipResource>>(), Arg.Any<CancellationToken>());
    }
}
