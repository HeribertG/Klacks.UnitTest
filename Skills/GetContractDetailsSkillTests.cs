// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetContractDetailsSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanViewContracts" }
    };

    [Test]
    public async Task ExistingContract_ReturnsDetails()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ContractResource
            {
                Id = contractId,
                Name = "Vollzeit 160",
                GuaranteedHours = 160m,
                MinimumHours = 140m,
                MaximumHours = 180m,
                ValidFrom = new DateTime(2026, 1, 1)
            });
        var skill = new GetContractDetailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Id").GetGuid().ShouldBe(contractId);
        data.GetProperty("Name").GetString().ShouldBe("Vollzeit 160");
        data.GetProperty("GuaranteedHours").GetDecimal().ShouldBe(160m);
        await mediator.Received(1).Send(
            Arg.Is<GetQuery<ContractResource>>(q => q.Id == contractId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());
        var skill = new GetContractDetailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("not found");
    }
}
