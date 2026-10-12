// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// accept_scenario, reject_scenario and propose_plan rely on their command handlers for the group-visibility check.
/// A scenario or group the caller cannot see comes back from the handler as KeyNotFoundException; the skills must
/// report it exactly like a missing scenario or group instead of letting it escape as an "Execution error".
/// </summary>

using Klacks.Api.Application.Commands.AnalyseScenarios;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScenarioSkillsVisibilityTests
{
    private const string MissingMessageFragment = "not exist";

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

    private Dictionary<string, object> ScenarioParams() => new() { ["scenarioId"] = _scenarioId.ToString() };

    [Test]
    public async Task AcceptScenario_OfHiddenScenario_ReportsItLikeAMissingOne()
    {
        _mediator.Send(Arg.Any<AcceptAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<AcceptAnalyseScenarioResult>(_ => throw new KeyNotFoundException($"AnalyseScenario with ID {_scenarioId} not found"));
        var skill = new AcceptScenarioSkill(_mediator, Substitute.For<IPendingConfirmationStore>(), Substitute.For<ITurnConfirmationScope>(), Substitute.For<ICompanyClock>());

        var result = await skill.ExecuteAsync(Ctx(), ScenarioParams());

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(MissingMessageFragment);
    }

    [Test]
    public async Task RejectScenario_OfHiddenScenario_ReportsItLikeAMissingOne()
    {
        _mediator.Send(Arg.Any<RejectAnalyseScenarioCommand>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new KeyNotFoundException($"AnalyseScenario with ID {_scenarioId} not found"));
        var skill = new RejectScenarioSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), ScenarioParams());

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(MissingMessageFragment);
    }

    [Test]
    public async Task ProposePlan_ForHiddenGroup_ReportsTheGroupAsNotFound()
    {
        var groupId = Guid.NewGuid();
        var notFound = $"Group with ID {groupId} not found";
        _mediator.Send(Arg.Any<ProposePlanCommand>(), Arg.Any<CancellationToken>())
            .Returns<ProposePlanOutcome>(_ => throw new KeyNotFoundException(notFound));
        var skill = new ProposePlanSkill(_mediator);
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = groupId.ToString(),
            ["fromDate"] = "2026-03-02",
            ["untilDate"] = "2026-03-08",
            ["placements"] = $"[{{\"clientId\":\"{Guid.NewGuid()}\",\"shiftId\":\"{Guid.NewGuid()}\",\"date\":\"2026-03-03\"}}]"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(notFound);
    }
}
