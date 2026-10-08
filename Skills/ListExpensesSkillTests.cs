// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for list_expenses: the skill sends ListExpensesInScopeQuery for the main plan by default or for the
/// scenario named by analyseToken, and projects id, workId, amount, description and taxable; an empty list yields a
/// zero-count success and an invalid token is refused before any read.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListExpensesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    [Test]
    public async Task List_WithoutToken_ListsTheMainPlan()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListExpensesInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpensesResource>
            {
                new() { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), Amount = 12.50m, Description = "Parking", Taxable = false },
                new() { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), Amount = 45m, Description = "Train ticket", Taxable = true }
            });
        var skill = new ListExpensesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 expense entries");
        await mediator.Received(1).Send(
            Arg.Is<ListExpensesInScopeQuery>(q => q.AnalyseToken == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_WithAScenarioToken_ListsThatScenario()
    {
        var token = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListExpensesInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpensesResource>());
        var skill = new ListExpensesSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["analyseToken"] = token.ToString() });

        await mediator.Received(1).Send(
            Arg.Is<ListExpensesInScopeQuery>(q => q.AnalyseToken == token), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_WithAnInvalidToken_ReturnsError_NoRead()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ListExpensesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["analyseToken"] = "nope" });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<ListExpensesInScopeQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListExpensesInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpensesResource>());
        var skill = new ListExpensesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 expense entries");
    }
}
