// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the thin evaluate_autonomy_level_change skill: parameter parsing (numeric/named
/// level, missing/invalid) and dispatch of EvaluateAutonomyLevelChangeQuery. The evaluation logic
/// itself is covered by EvaluateAutonomyLevelChangeQueryHandlerTests.
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateAutonomyLevelChangeSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static AutonomyLevelChangeEvaluationResult Result(int newlyUnconfirmed) => new(
        CurrentLevel: 1, CurrentLevelName: "Assisted",
        TargetLevel: 2, TargetLevelName: "Autonomous",
        IsNoOp: false, IsDowngrade: false,
        Impacts: [],
        SkillsNewlyUnconfirmedInChat: newlyUnconfirmed, SkillsNewlyConfirmedInChat: 0,
        Recommendation: "Raising the level from Assisted to Autonomous lets 1 currently registered skill(s) run without asking first.");

    [Test]
    public async Task NumericLevel_SendsQueryAndRelaysRecommendation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateAutonomyLevelChangeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result(1));
        var skill = new EvaluateAutonomyLevelChangeSkill(mediator);
        var context = Ctx();

        var result = await skill.ExecuteAsync(context, new Dictionary<string, object> { ["level"] = 2 });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Raising the level"));
        await mediator.Received(1).Send(
            Arg.Is<EvaluateAutonomyLevelChangeQuery>(q => q.UserId == context.UserId && q.TargetLevel == AutonomyLevel.Autonomous),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NamedLevel_IsParsedCaseInsensitive()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<EvaluateAutonomyLevelChangeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result(0));
        var skill = new EvaluateAutonomyLevelChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = "fullyautonomous" });

        Assert.That(result.Success, Is.True);
        await mediator.Received(1).Send(
            Arg.Is<EvaluateAutonomyLevelChangeQuery>(q => q.TargetLevel == AutonomyLevel.FullyAutonomous),
            Arg.Any<CancellationToken>());
    }

    [TestCase("4")]
    [TestCase("-1")]
    [TestCase("totally_autonomous")]
    public async Task InvalidLevel_ReturnsErrorWithoutDispatch(string level)
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new EvaluateAutonomyLevelChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["level"] = level });

        Assert.That(result.Success, Is.False);
        await mediator.DidNotReceive().Send(Arg.Any<EvaluateAutonomyLevelChangeQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingLevel_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new EvaluateAutonomyLevelChangeSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        await mediator.DidNotReceive().Send(Arg.Any<EvaluateAutonomyLevelChangeQuery>(), Arg.Any<CancellationToken>());
    }
}
