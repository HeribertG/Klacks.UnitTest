// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the thin evaluate_scenario skill: parameter validation (needs scenarioId or
/// analyseToken), dispatch of EvaluateScenarioQuery and the not-found / success projection. The
/// evaluation logic itself is covered by EvaluateScenarioQueryHandlerTests.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateScenarioSkillTests
{
    private static readonly Guid Token = Guid.NewGuid();
    private static readonly Guid ScenarioId = Guid.NewGuid();

    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<EvaluateScenarioQuery>(), Arg.Any<CancellationToken>())
            .Returns(Found());
    }

    private static ScenarioEvaluationResult Found() => new(
        Found: true, ScenarioId: ScenarioId, Token: Token, Name: "Proposal", Status: "Active",
        GroupId: Guid.NewGuid(), FromDate: "2026-03-02", UntilDate: "2026-03-08",
        TotalConflicts: 0, Errors: 0, Warnings: 0, Info: 0, RuleCompliant: true,
        ByCode: new Dictionary<string, int>(), Conflicts: [], ConflictsTruncated: false,
        RealEntryCount: 5, ScenarioEntryCount: 6, AddedEntryCount: 1, RemovedEntryCount: 0,
        AddedWorkEntries: 1, AddedReplacementEntries: 0, AddedBreakEntries: 0,
        AddedByType: new Dictionary<string, int> { ["Work"] = 1 }, AddedEntries: [], RemovedEntries: [],
        ChangesTruncated: false, Recommendation: "Scenario is rule-clean and introduces 1 change(s).");

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private EvaluateScenarioSkill Skill() => new(_mediator);

    [Test]
    public async Task DispatchesQuery_WithToken_AndProjects()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["analyseToken"] = Token.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Proposal");
        result.Message.ShouldContain("rule-clean");
        await _mediator.Received(1).Send(
            Arg.Is<EvaluateScenarioQuery>(q => q.Token == Token), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchesQuery_WithScenarioId()
    {
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["scenarioId"] = ScenarioId.ToString()
        });

        await _mediator.Received(1).Send(
            Arg.Is<EvaluateScenarioQuery>(q => q.ScenarioId == ScenarioId && q.Token == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingBothParams_ReturnsError()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("scenarioId or analyseToken");
    }

    [Test]
    public async Task InvalidScenarioId_ReturnsError()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["scenarioId"] = "not-a-guid"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("scenarioId");
    }

    [Test]
    public async Task NotFound_ReturnsError()
    {
        _mediator.Send(Arg.Any<EvaluateScenarioQuery>(), Arg.Any<CancellationToken>())
            .Returns(ScenarioEvaluationResult.NotFound());

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["analyseToken"] = Token.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No scenario found");
    }
}
