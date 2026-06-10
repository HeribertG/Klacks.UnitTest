// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListClientMembershipsSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    [Test]
    public async Task ListAll_ReturnsAllMemberships()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MembershipResource>
            {
                new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Type = 0, ValidFrom = new DateTime(2026, 1, 1) },
                new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Type = 0, ValidFrom = new DateTime(2026, 2, 1) }
            }.AsEnumerable());
        var skill = new ListClientMembershipsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
        await mediator.Received(1).Send(Arg.Any<ListQuery<MembershipResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListByClientId_FiltersToThatClient()
    {
        var targetClientId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<MembershipResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MembershipResource>
            {
                new() { Id = Guid.NewGuid(), ClientId = targetClientId, Type = 0, ValidFrom = new DateTime(2026, 3, 1) },
                new() { Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), Type = 0, ValidFrom = new DateTime(2026, 1, 1) }
            }.AsEnumerable());
        var skill = new ListClientMembershipsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = targetClientId.ToString()
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(1);
        data.GetProperty("Memberships")[0].GetProperty("ClientId").GetGuid().ShouldBe(targetClientId);
    }

    [Test]
    public async Task InvalidClientIdFormat_ReturnsError_NoQuery()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ListClientMembershipsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = "not-a-guid"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<ListQuery<MembershipResource>>(), Arg.Any<CancellationToken>());
    }
}
