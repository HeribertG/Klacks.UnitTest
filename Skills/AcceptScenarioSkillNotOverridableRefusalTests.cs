// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// accept_scenario on a refusal no confirmation lifts: a stale cover of an absence built on the original plan and an
/// accept that would delete a real absence. The skill mints no confirmation token for them, marks nothing as issued and
/// returns an error that tells the model the way out (cover again, recalculate), including the cover's prefill and the
/// missing absences.
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AcceptScenarioSkillNotOverridableRefusalTests
{
    private static readonly DateTime LastChangeUtc = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AbsentClientId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private IMediator _mediator = null!;
    private IPendingConfirmationStore _store = null!;
    private ITurnConfirmationScope _turnScope = null!;
    private AcceptScenarioSkill _skill = null!;
    private Guid _scenarioId;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _store = Substitute.For<IPendingConfirmationStore>();
        _turnScope = Substitute.For<ITurnConfirmationScope>();
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTimeZoneAsync(Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        _skill = new AcceptScenarioSkill(_mediator, _store, _turnScope, companyClock);
        _scenarioId = Guid.NewGuid();
    }

    [Test]
    public async Task StaleCoverOnTheOriginalPlan_GetsNoToken_AndTellsToCoverAgain()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw ScenarioStaleException.NotOverridable(
                new ScenarioStaleness(2, LastChangeUtc, new[] { "Cover A" }),
                new CoverRedoPreset(AbsentClientId, Guid.NewGuid(), new DateOnly(2026, 5, 12), new DateOnly(2026, 5, 13))));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("cannot be accepted");
        result.Message.ShouldContain("cover the absence again");
        result.Message.ShouldContain("Cover A");
        result.Message.ShouldContain(AbsentClientId.ToString());
        result.Message.ShouldContain("2026-05-12 to 2026-05-13");
        AssertNoTokenIssued();
    }

    [Test]
    public async Task ScenarioThatDropsARealAbsence_GetsNoToken_AndNamesTheMissingAbsence()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw new ScenarioDropsRealAbsenceException(
                3, new[] { new DroppedRealAbsence(AbsentClientId, "Elsa Muster", new DateOnly(2026, 5, 12), Guid.NewGuid()) }));

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Type.ShouldBe(SkillResultType.Error);
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("3 real absence(s)");
        result.Message.ShouldContain("Elsa Muster 2026-05-12");
        result.Message.ShouldContain("recalculated");
        AssertNoTokenIssued();
    }

    [Test]
    public async Task ReplayedConfirmation_ThatNowDropsARealAbsence_GetsNoNewToken()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw new ScenarioDropsRealAbsenceException(
                1, new[] { new DroppedRealAbsence(AbsentClientId, "Elsa Muster", new DateOnly(2026, 5, 12), Guid.NewGuid()) }));

        var result = await _skill.ExecuteAsync(
            Ctx() with { BypassAutonomyGate = true, StaleScenarioOverwriteAcknowledgement = new StaleScenarioOverwriteAcknowledgement(1, LastChangeUtc) },
            Params());

        result.Type.ShouldBe(SkillResultType.Error);
        AssertNoTokenIssued();
    }

    private void AssertNoTokenIssued()
    {
        _store.DidNotReceiveWithAnyArgs().Create(default, default!, default!, default!, default!, default);
        _turnScope.DidNotReceiveWithAnyArgs().MarkIssued(default!);
    }

    private SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
    };

    private Dictionary<string, object> Params()
        => new() { ["scenarioId"] = _scenarioId.ToString() };
}
