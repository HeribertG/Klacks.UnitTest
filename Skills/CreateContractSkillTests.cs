// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateContractSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static IMediator MediatorReturningCreated()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var resource = call.Arg<PostCommand<ContractResource>>().Resource;
                resource.Id = Guid.NewGuid();
                return resource;
            });
        return mediator;
    }

    [Test]
    public async Task ExplicitValues_CreatesTemplate_WithDefaultsApplied()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Standard 80%",
            ["guaranteedHours"] = 134.4m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.Name == "Standard 80%" &&
                c.Resource.GuaranteedHours == 134.4m &&
                c.Resource.MinimumHours == 134.4m &&
                c.Resource.MaximumHours == 134.4m &&
                c.Resource.FullTime == decimal.Zero &&
                c.Resource.PaymentInterval == PaymentInterval.Monthly &&
                c.Resource.ValidUntil == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitRangeAndInterval_ArePassedThrough()
    {
        var mediator = MediatorReturningCreated();
        var skill = new CreateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Flex",
            ["guaranteedHours"] = 100m,
            ["minimumHours"] = 80m,
            ["maximumHours"] = 120m,
            ["fullTime"] = 168m,
            ["paymentInterval"] = "weekly",
            ["validFrom"] = "2026-07-01",
            ["validUntil"] = "2026-12-31"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<ContractResource>>(c =>
                c.Resource.MinimumHours == 80m &&
                c.Resource.MaximumHours == 120m &&
                c.Resource.FullTime == 168m &&
                c.Resource.PaymentInterval == PaymentInterval.Weekly &&
                c.Resource.ValidUntil != null),
            Arg.Any<CancellationToken>());
    }

    [TestCase("name")]
    [TestCase("guaranteedHours")]
    [TestCase("validFrom")]
    public async Task MissingRequiredParameter_ReturnsErrorWithoutMutation(string missing)
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator);
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "2026-07-01"
        };
        parameters.Remove(missing);

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MinAboveMax_ReturnsErrorWithoutMutation()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["minimumHours"] = 120m,
            ["maximumHours"] = 80m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GuaranteedOutsideRange_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 130m,
            ["minimumHours"] = 80m,
            ["maximumHours"] = 120m,
            ["validFrom"] = "2026-07-01"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task ValidUntilBeforeValidFrom_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "2026-07-01",
            ["validUntil"] = "2026-06-01"
        });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task InvalidPaymentInterval_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateContractSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "X",
            ["guaranteedHours"] = 100m,
            ["validFrom"] = "2026-07-01",
            ["paymentInterval"] = "yearly"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>());
    }
}
