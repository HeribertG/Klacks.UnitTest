// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_expenses: the skill sends ListQuery&lt;ExpensesResource&gt; and projects
/// id, workId, amount, description and taxable; an empty list yields a zero-count success.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries;
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
    public async Task List_ReturnsExpenses()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<ExpensesResource>>(), Arg.Any<CancellationToken>())
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
            Arg.Any<ListQuery<ExpensesResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<ExpensesResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExpensesResource>());
        var skill = new ListExpensesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 expense entries");
    }
}
