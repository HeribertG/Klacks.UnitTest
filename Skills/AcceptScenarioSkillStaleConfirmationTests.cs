// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// accept_scenario on a stale scenario (the original plan changed after it was created): the skill does not accept, it
/// stores the call under a one-time token of purpose StaleScenarioOverwrite together with the staleness the user is
/// shown, and asks for the user's explicit consent. Only the acknowledgement that confirm_pending_action puts on the
/// context unlocks OverrideStale; the former hidden parameter does nothing, not even on a gate-bypassing call (plan
/// step, scheduled task, inbound, proactive).
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

    private static readonly DateTime LastChangeUtc = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

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
    public async Task StaleScenario_AsksForConfirmation_AndStoresTheShownStalenessUnderItsOwnPurpose()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw new ScenarioStaleException(
                new ScenarioStaleness(4, LastChangeUtc, new[] { "Scenario B" })));

        var result = await _skill.ExecuteAsync(Ctx(bypassGate: false), Params(withLegacyFlag: true));

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Confirmation);
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(IssuedToken);
        result.Message.ShouldContain("Scenario B");
        result.Message.ShouldContain("4 changed entries");
        result.Message.ShouldContain("2026-04-20 10:00 company time");
        _store.Received(1).Create(
            _userId,
            ScenarioAcceptDefaults.SkillName,
            Arg.Is<IReadOnlyDictionary<string, object>>(p =>
                (string)p[ScenarioAcceptDefaults.AcknowledgedChangedEntryCountParameter] == "4"
                && (string)p[ScenarioAcceptDefaults.AcknowledgedLastChangeUtcTicksParameter] == LastChangeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && !p.ContainsKey(ScenarioAcceptDefaults.LegacyStaleConfirmedParameter)
                && (string)p["scenarioId"] == _scenarioId.ToString()),
            PendingConfirmationPurposes.StaleScenarioOverwrite);
        _turnScope.Received(1).MarkIssued(IssuedToken);
    }

    [Test]
    public async Task ConfirmedReplay_AcceptsWithOverrideStale_AndPassesTheShownStaleness()
    {
        var acknowledgement = new StaleScenarioOverwriteAcknowledgement(4, LastChangeUtc);
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(new AcceptAnalyseScenarioResult(true));

        var result = await _skill.ExecuteAsync(
            Ctx(bypassGate: true) with { StaleScenarioOverwriteAcknowledgement = acknowledgement }, Params());

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(c => c.ScenarioId == _scenarioId
                && c.OverrideStale
                && !c.OverrideBlock
                && c.AcknowledgedStaleness == acknowledgement),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GateBypassingCallWithTheFormerHiddenParameter_GetsNoOverride()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(new AcceptAnalyseScenarioResult(true));

        await _skill.ExecuteAsync(Ctx(bypassGate: true), Params(withLegacyFlag: true));

        await _mediator.Received(1).Send(
            Arg.Is<AcceptAnalyseScenarioCommand>(c => !c.OverrideStale && c.AcknowledgedStaleness == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DirectCallWithTheFormerHiddenParameter_GetsNoOverride()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>()).Returns(new AcceptAnalyseScenarioResult(true));

        await _skill.ExecuteAsync(Ctx(bypassGate: false), Params(withLegacyFlag: true));

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

    private Dictionary<string, object> Params(bool withLegacyFlag = false)
    {
        var parameters = new Dictionary<string, object> { ["scenarioId"] = _scenarioId.ToString() };
        if (withLegacyFlag)
        {
            parameters[ScenarioAcceptDefaults.LegacyStaleConfirmedParameter] = "true";
        }

        return parameters;
    }
}
