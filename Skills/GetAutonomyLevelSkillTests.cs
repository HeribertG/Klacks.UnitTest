// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetAutonomyLevelSkillTests
{
    [Test]
    public async Task ReturnsLevelFromQuery()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetAutonomyLevelQuery>(), Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Assisted);
        var skill = new GetAutonomyLevelSkill(mediator);
        var context = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "tester",
            UserPermissions = new List<string>()
        };

        var result = await skill.ExecuteAsync(context, new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("1"));
        await mediator.Received(1).Send(
            Arg.Is<GetAutonomyLevelQuery>(q => q.UserId == context.UserId),
            Arg.Any<CancellationToken>());
    }
}
