// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for update_expense: the skill loads the expense via GetQuery, patches only the
/// supplied fields and persists via PutCommand; missing expenses and no-op calls do not mutate.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateExpenseSkillTests
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

    private static ExpensesResource Existing() => new()
    {
        Id = ExpenseId,
        WorkId = WorkId,
        Amount = 20m,
        Description = "Parking",
        Taxable = false
    };

    [Test]
    public async Task Update_OnlyAmount_PreservesOtherFields()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExpenseInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing());
        ExpensesResource? captured = null;
        mediator.Send(Arg.Do<PutCommand<ExpensesResource>>(c => captured = c.Resource), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ExpensesResource>)ci[0]).Resource);
        var skill = new UpdateExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString(),
            ["amount"] = 35m
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("amount");
        captured.ShouldNotBeNull();
        captured!.Id.ShouldBe(ExpenseId);
        captured.WorkId.ShouldBe(WorkId);
        captured.Amount.ShouldBe(35m);
        captured.Description.ShouldBe("Parking");
        captured.Taxable.ShouldBeFalse();
    }

    [Test]
    public async Task Update_WithAScenarioToken_ReadsTheExpenseInThatScenario()
    {
        var token = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExpenseInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing());
        mediator.Send(Arg.Any<PutCommand<ExpensesResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<ExpensesResource>)ci[0]).Resource);
        var skill = new UpdateExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString(),
            ["amount"] = 35m,
            ["analyseToken"] = token.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetExpenseInScopeQuery>(q => q.Id == ExpenseId && q.AnalyseToken == token),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_WithoutAToken_ReadsTheExpenseInTheMainPlan()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExpenseInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing());
        var skill = new UpdateExpenseSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["expenseId"] = ExpenseId.ToString() });

        await mediator.Received(1).Send(
            Arg.Is<GetExpenseInScopeQuery>(q => q.Id == ExpenseId && q.AnalyseToken == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_WithAnInvalidToken_ReturnsError_NoReadNoWrite()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString(),
            ["amount"] = 35m,
            ["analyseToken"] = "not-a-uuid"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("analyseToken");
        await mediator.DidNotReceive().Send(Arg.Any<GetExpenseInScopeQuery>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<ExpensesResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_NoFieldsSupplied_NoOp_DoesNotPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExpenseInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing());
        var skill = new UpdateExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("No fields");
        await mediator.DidNotReceive().Send(
            Arg.Any<PutCommand<ExpensesResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Update_ExpenseNotFound_ReturnsError_NoMutation()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExpenseInScopeQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KeyNotFoundException($"Expenses with ID {ExpenseId} not found"));
        var skill = new UpdateExpenseSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["expenseId"] = ExpenseId.ToString(),
            ["amount"] = 35m
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await mediator.DidNotReceive().Send(
            Arg.Any<PutCommand<ExpensesResource>>(), Arg.Any<CancellationToken>());
    }
}
