// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// accept_scenario on a stale scenario (the original plan changed after it was created): the skill does not accept, it
/// stores the call under a one-time confirmation token with the hidden stale acknowledgement and asks for the user's
/// explicit consent. Only the confirmed replay (gate bypassed by confirm_pending_action) accepts with OverrideStale; a
/// model that adds the hidden parameter to a direct call gets no override.
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
public class AcceptScenarioSkillStaleConfirmationTests
{
    private const string IssuedToken = "token-1";

    private IMediator _mediator = null!;
    private IPendingConfirmationStore _store = null!;
    private ITurnConfirmationScope _turnScope = null!;
    private AcceptScenarioSkill _skill = null!;
    private Guid _scenarioId;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _store = Substitute.For<IPendingConfirmationStore>();
        _store.Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>(), Arg.Any<string>())
            .Returns(IssuedToken);
        _turnScope = Substitute.For<ITurnConfirmationScope>();
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTimeZoneAsync(Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _skill = new AcceptScenarioSkill(_mediator, _store, _turnScope, companyClock);
        _scenarioId = Guid.NewGuid();
        _userId = Guid.NewGuid();
    }

    [Test]
    public async Task StaleScenario_AsksForConfirmation_AndStoresTheAcknowledgedCall()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new ScenarioStaleException(
                new ScenarioStaleness(4, new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc), new[] { "Scenario B" })));

        var result = await _skill.ExecuteAsync(Ctx(bypassGate: false), Params());

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(IssuedToken);
        result.Message.ShouldContain("Scenario B");
        result.Message.ShouldContain("4 changed entries");
        result.Message.ShouldContain("2026-04-20 10:00 company time");
        _store.Received(1).Create(
            _userId,
            "accept_scenario",
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                (string)p[ScenarioAcceptDefaults.StaleConfirmedParameter] == ScenarioAcceptDefaults.ConfirmedValue
                && (string)p["scenarioId"] == _scenarioId.ToString()),
            Arg.Any<string>());
        _turnScope.Received(1).MarkIssuedForSensitiveSkill(IssuedToken);
        _turnScope.Received(1).MarkIssued(IssuedToken);
    }

    [Test]
    public async Task ConfirmedReplay_AcceptsWithOverrideStale()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(bypassGate: true), Params(withAcknowledgement: true));

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(c => c.ScenarioId == _scenarioId && c.OverrideStale && !c.OverrideBlock),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DirectCallWithTheHiddenParameter_GetsNoOverride()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(true);

        await _skill.ExecuteAsync(Ctx(bypassGate: false), Params(withAcknowledgement: true));

        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(c => !c.OverrideStale),
            Arg.Any<CancellationToken>());
    }

    private SkillExecutionContext Ctx(bool bypassGate) => new()
    {
        UserId = _userId,
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
        BypassAutonomyGate = bypassGate
    };

    private Dictionary<string, object> Params(bool withAcknowledgement = false)
    {
        var parameters = new Dictionary<string, object> { ["scenarioId"] = _scenarioId.ToString() };
        if (withAcknowledgement)
        {
            parameters[ScenarioAcceptDefaults.StaleConfirmedParameter] = ScenarioAcceptDefaults.ConfirmedValue;
        }

        return parameters;
    }
}
