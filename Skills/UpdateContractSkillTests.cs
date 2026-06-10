// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateContractSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditContracts" }
    };

    private static ContractResource Contract(Guid id) => new()
    {
        Id = id,
        Name = "Vollzeit 160",
        GuaranteedHours = 160m,
        MinimumHours = 140m,
        MaximumHours = 180m,
        FullTime = 160m,
        ValidFrom = new DateTime(2026, 1, 1),
        ValidUntil = null
    };

    [Test]
    public async Task UpdateNameAndHours_DispatchesPutCommand_WithMergedValues()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        mediator.Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ContractResource>)ci[0]).Resource);
        var skill = new UpdateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["name"] = "Vollzeit 170",
            ["guaranteedHours"] = 170m,
            ["maximumHours"] = 190m
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<ContractResource>>(c =>
                c.Resource.Id == contractId &&
                c.Resource.Name == "Vollzeit 170" &&
                c.Resource.GuaranteedHours == 170m &&
                c.Resource.MaximumHours == 190m &&
                c.Resource.MinimumHours == 140m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MinimumAboveMaximum_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["minimumHours"] = 200m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NegativeHours_ReturnsError_NoMutation()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString(),
            ["guaranteedHours"] = -5m
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = Guid.NewGuid().ToString(),
            ["name"] = "Renamed"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoFieldsSupplied_ReturnsSuccess_WithoutPut()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(Contract(contractId));
        var skill = new UpdateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }
}
