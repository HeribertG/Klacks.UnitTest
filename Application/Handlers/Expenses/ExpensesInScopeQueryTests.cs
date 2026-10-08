// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// update_expense / delete_expense used to read through GetQuery&lt;ExpensesResource&gt;, which only sees the main
/// plan, so scenario expenses were unreachable for Klacksy. GetExpenseInScopeQuery reads in exactly one scope;
/// another scope, a hidden owner and a missing expense are answered alike.
/// </summary>

using Klacks.Api.Application.Handlers.Expenses;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Schedules;
using Microsoft.Extensions.Logging;
using ExpensesEntity = Klacks.Api.Domain.Models.Schedules.Expenses;

namespace Klacks.UnitTest.Application.Handlers.Expenses;

[TestFixture]
public class ExpensesInScopeQueryTests
{
    private IExpensesRepository _repository = null!;
    private IClientVisibilityGuard _visibilityGuard = null!;
    private Guid _hiddenClientId;
    private GetInScopeQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IExpensesRepository>();
        _visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _hiddenClientId = Guid.NewGuid();
        _visibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Guid>(0) != _hiddenClientId);
        _handler = new GetInScopeQueryHandler(
            _repository, _visibilityGuard, new ScheduleMapper(), Substitute.For<ILogger<GetInScopeQueryHandler>>());
    }

    [Test]
    public async Task ScenarioExpense_InItsScenario_IsReturned()
    {
        var token = Guid.NewGuid();
        var expense = Given(Guid.NewGuid(), token);

        var result = await _handler.Handle(new GetExpenseInScopeQuery(expense.Id, token), CancellationToken.None);

        result.Id.ShouldBe(expense.Id);
    }

    [Test]
    public async Task LegacyExpenseWithoutOwnToken_OnAScenarioWork_BelongsToThatScenario()
    {
        var token = Guid.NewGuid();
        var expense = Given(Guid.NewGuid(), token);
        expense.AnalyseToken = null;

        var result = await _handler.Handle(new GetExpenseInScopeQuery(expense.Id, token), CancellationToken.None);
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetExpenseInScopeQuery(expense.Id, null), CancellationToken.None));

        result.Id.ShouldBe(expense.Id);
    }

    [Test]
    public async Task MainPlanExpense_WithoutToken_IsReturned()
    {
        var expense = Given(Guid.NewGuid(), null);

        var result = await _handler.Handle(new GetExpenseInScopeQuery(expense.Id, null), CancellationToken.None);

        result.Id.ShouldBe(expense.Id);
    }

    [Test]
    public async Task OtherScope_HiddenOwner_AndMissing_AreAnsweredAlike()
    {
        var scenarioExpense = Given(Guid.NewGuid(), Guid.NewGuid());
        var hiddenExpense = Given(_hiddenClientId, null);
        var missingId = Guid.NewGuid();

        var otherScope = await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetExpenseInScopeQuery(scenarioExpense.Id, null), CancellationToken.None));
        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetExpenseInScopeQuery(hiddenExpense.Id, null), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetExpenseInScopeQuery(missingId, null), CancellationToken.None));

        otherScope.Message.ShouldBe($"Expenses with ID {scenarioExpense.Id} not found");
        hidden.Message.ShouldBe($"Expenses with ID {hiddenExpense.Id} not found");
        missing.Message.ShouldBe($"Expenses with ID {missingId} not found");
    }

    private ExpensesEntity Given(Guid clientId, Guid? analyseToken)
    {
        var work = new Work { Id = Guid.NewGuid(), ClientId = clientId, AnalyseToken = analyseToken };
        var expense = new ExpensesEntity { Id = Guid.NewGuid(), WorkId = work.Id, Work = work, AnalyseToken = analyseToken };
        _repository.GetWithWorkInAnyScope(expense.Id).Returns(expense);
        return expense;
    }
}
