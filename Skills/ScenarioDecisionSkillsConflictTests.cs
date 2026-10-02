// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// accept_scenario and reject_scenario relay a refused decision as a plain skill error carrying the
/// handler's reason - a scenario that is no longer active, or a compliance block on accept - instead of
/// letting the conflict escape as an unexpected "Execution error".
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScenarioDecisionSkillsConflictTests
{
    private const string NotActiveMessage = "Scenario is no longer active (status Accepted).";
    private const string BlockedMessage = "Scenario acceptance blocked: 2 compliance violation(s) enforced in Block mode.";

    private IMediator _mediator = null!;
    private Guid _scenarioId;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _scenarioId = Guid.NewGuid();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    private Dictionary<string, object> Params() => new() { ["scenarioId"] = _scenarioId.ToString() };

    [Test]
    public async Task AcceptScenario_OfANonActiveScenario_ReturnsTheHandlersReasonAsError()
    {
        // Arrange
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ScenarioNotActiveException(NotActiveMessage));
        var skill = new AcceptScenarioSkill(_mediator);

        // Act
        var result = await skill.ExecuteAsync(Ctx(), Params());

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(NotActiveMessage);
    }

    [Test]
    public async Task AcceptScenario_BlockedByCompliance_ReturnsTheHandlersReasonAsError()
    {
        // Arrange
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ConflictException(BlockedMessage));
        var skill = new AcceptScenarioSkill(_mediator);

        // Act
        var result = await skill.ExecuteAsync(Ctx(), Params());

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(BlockedMessage);
    }

    [Test]
    public async Task RejectScenario_OfANonActiveScenario_ReturnsTheHandlersReasonAsError()
    {
        // Arrange
        _mediator.Send(Arg.Any<RejectAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ScenarioNotActiveException(NotActiveMessage));
        var skill = new RejectScenarioSkill(_mediator);

        // Act
        var result = await skill.ExecuteAsync(Ctx(), Params());

        // Assert
        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(NotActiveMessage);
    }

    [Test]
    public async Task AcceptScenario_OfAnActiveScenario_StillSucceeds()
    {
        // Arrange
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new AcceptScenarioSkill(_mediator);

        // Act
        var result = await skill.ExecuteAsync(Ctx(), Params());

        // Assert
        result.Success.ShouldBeTrue();
    }
}
