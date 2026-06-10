// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetAutonomyLevelSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task NumericLevel_SendsCommandAndSucceeds()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetAutonomyLevelCommand>(), Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);
        var skill = new SetAutonomyLevelSkill(mediator);
        var context = Ctx();

        var result = await skill.ExecuteAsync(context, new Dictionary<string, object> { ["level"] = 3 });

        Assert.That(result.Success, Is.True);
        await mediator.Received(1).Send(
            Arg.Is<SetAutonomyLevelCommand>(c => c.UserId == context.UserId && c.Level == AutonomyLevel.FullyAutonomous),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NamedLevel_IsParsedCaseInsensitive()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetAutonomyLevelCommand>(), Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Assisted);
        var skill = new SetAutonomyLevelSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = "assisted" });

        Assert.That(result.Success, Is.True);
        await mediator.Received(1).Send(
            Arg.Is<SetAutonomyLevelCommand>(c => c.Level == AutonomyLevel.Assisted),
            Arg.Any<CancellationToken>());
    }

    [TestCase("4")]
    [TestCase("-1")]
    [TestCase("totally_autonomous")]
    public async Task InvalidLevel_ReturnsErrorWithoutMutation(string level)
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetAutonomyLevelSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = level });

        Assert.That(result.Success, Is.False);
        await mediator.DidNotReceive().Send(Arg.Any<SetAutonomyLevelCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingLevel_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetAutonomyLevelSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        await mediator.DidNotReceive().Send(Arg.Any<SetAutonomyLevelCommand>(), Arg.Any<CancellationToken>());
    }
}
