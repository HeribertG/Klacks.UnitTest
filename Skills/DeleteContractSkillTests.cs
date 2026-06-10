// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteContractSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanDeleteContracts" }
    };

    [Test]
    public async Task ExistingContract_DispatchesDeleteCommand()
    {
        var contractId = Guid.NewGuid();
        var contract = new ContractResource { Id = contractId, Name = "Vollzeit 160" };
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(contract);
        mediator.Send(Arg.Any<DeleteCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(contract);
        var skill = new DeleteContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("Vollzeit 160");
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<ContractResource>>(c => c.Id == contractId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_ReturnsError_NoDeletion()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());
        var skill = new DeleteContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }
}
