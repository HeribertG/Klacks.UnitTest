// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_expense: the skill sends DeleteCommand&lt;ExpensesResource&gt; with the
/// expense id and reports the deleted entry; a null handler result means not found.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteExpenseSkillTests
{
    private static readonly Guid ExpenseId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task Delete_ExistingExpense_SendsDeleteCommand()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ExpensesResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ExpensesResource
            {
                Id = ExpenseId,
                WorkId = WorkId,
                Amount = 20m,
                Description = "Parking",
                Taxable = false
            });
        var skill = new DeleteExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("deleted");
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<ExpensesResource>>(c => c.Id == ExpenseId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_ExpenseNotFound_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<ExpensesResource>>(), Arg.Any<CancellationToken>())
            .Returns((ExpensesResource?)null);
        var skill = new DeleteExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }
}
