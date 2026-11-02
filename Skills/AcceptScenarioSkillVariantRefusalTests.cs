// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// accept_scenario on a scenario that carries variant keys: a refusal for a missing right or session (Slack bridge, plan
/// step, scheduled task) and a consumed key come back as an error result with the English refusal message instead of an
/// unhandled failure, and no confirmation is stored. The skill has no parameter that could bypass the rule: overrideBlock
/// and overrideStale in the call do not reach the command.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AcceptScenarioSkillVariantRefusalTests
{
    private IMediator _mediator = null!;
    private IPendingConfirmationStore _store = null!;
    private AcceptScenarioSkill _skill = null!;
    private Guid _scenarioId;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _store = Substitute.For<IPendingConfirmationStore>();
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTimeZoneAsync(Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _skill = new AcceptScenarioSkill(_mediator, _store, Substitute.For<ITurnConfirmationScope>(), companyClock);
        _scenarioId = Guid.NewGuid();
    }

    [Test]
    public async Task MissingRightOrSession_ReturnsAnErrorResultWithTheForbiddenMessage()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw new ForbiddenException(ScenarioAcceptDefaults.VariantAcceptForbiddenMessage));

        var result = await _skill.ExecuteAsync(Ctx(bypassGate: true), Params());

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldBe($"Failed to accept scenario {_scenarioId}: {ScenarioAcceptDefaults.VariantAcceptForbiddenMessage}");
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ConsumedVariant_ReturnsAnErrorResultNamingTheConsumedKeys()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw new ScenarioVariantConsumedException(new[] { "Winter keys", "Zeta keys" }));

        var result = await _skill.ExecuteAsync(Ctx(bypassGate: false), Params());

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldBe(
            $"Failed to accept scenario {_scenarioId}: "
            + string.Format(ScenarioAcceptDefaults.VariantConsumedMessageFormat, "Winter keys, Zeta keys"));
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task OverrideParametersInTheCall_DoNotReachTheCommand()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(new AcceptAnalyseScenarioResult(true));
        var parameters = Params();
        parameters["overrideBlock"] = "true";
        parameters["overrideStale"] = "true";

        await _skill.ExecuteAsync(Ctx(bypassGate: true), parameters);

        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(c => c.ScenarioId == _scenarioId && !c.OverrideBlock && !c.OverrideStale),
            Arg.Any<CancellationToken>());
    }

    private static SkillExecutionContext Ctx(bool bypassGate) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { Permissions.CanEditShifts },
        BypassAutonomyGate = bypassGate
    };

    private Dictionary<string, object> Params() => new() { ["scenarioId"] = _scenarioId.ToString() };
}
