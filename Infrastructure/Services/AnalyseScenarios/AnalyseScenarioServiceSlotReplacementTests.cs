// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Wizard 1 slot replacement never drops expenses: a displaced incumbent's expenses (taxable and not) move to the
/// planner's work on the same slot and are counted when that work belongs to another employee. A slot that received no
/// planner work is emptied; only an incumbent carrying expenses without another work of its employee that day stays.
/// A corrected work (correction WorkChange) is fixed and never displaced.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceSlotReplacementTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();
    private static readonly Guid AgentC = Guid.NewGuid();
    private static readonly Guid AgentD = Guid.NewGuid();
    private static readonly Guid AgentE = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _sourceWorkId = Guid.NewGuid();
    private Work _incumbent = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = AnalyseScenarioTestServiceFactory.Create(_context);

        _incumbent = NewWork(AgentA);
        _incumbent.ClonedFromWorkId = _sourceWorkId;
        _context.Work.Add(_incumbent);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ReplacedIncumbent_ExpensesMoveToThePlannersWorkOnTheSameSlot()
    {
        await AddIncumbentExpensesAsync();
        var displaced = await DisplaceAsync();
        var successor = await AddCreatedWorkAsync(AgentB, _shiftId);

        var changedEmployee = await _service.ResolveDisplacedWorksAsync(_token, displaced, [successor.Id], CancellationToken.None);
        await _context.SaveChangesAsync();

        var expenses = await _context.Expenses.Where(e => !e.IsDeleted && e.WorkId == successor.Id).ToListAsync();
        expenses.Select(e => e.Taxable).ShouldBe(new[] { false, true }, ignoreOrder: true);
        expenses.ShouldAllBe(e => e.AnalyseToken == _token);
        changedEmployee.ShouldBe(2);
        (await _context.Work.SingleAsync(w => w.Id == successor.Id)).ClonedFromWorkId.ShouldBe(_sourceWorkId);
        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == _incumbent.Id)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task ReplacedIncumbent_ExpensesReplaceTheShiftDefaultsBulkAddStagedOnThePlannersWork()
    {
        await AddIncumbentExpensesAsync();
        var displaced = await DisplaceAsync();
        var successor = NewWork(AgentB);
        _context.Work.Add(successor);
        _context.Expenses.Add(NewExpense(successor.Id, taxable: false));
        await _context.SaveChangesAsync();

        await _service.ResolveDisplacedWorksAsync(_token, displaced, [successor.Id], CancellationToken.None);
        await _context.SaveChangesAsync();

        (await _context.Expenses.Where(e => !e.IsDeleted && e.WorkId == successor.Id).Select(e => e.Taxable).ToListAsync())
            .ShouldBe(new[] { false, true }, ignoreOrder: true, "exactly the incumbent's expenses, the default is not doubled");
    }

    [Test]
    public async Task BlockedSlot_IncumbentWithoutExpenses_IsEmptied()
    {
        await _context.SaveChangesAsync();
        var displaced = await DisplaceAsync();

        await _service.ResolveDisplacedWorksAsync(_token, displaced, [], CancellationToken.None);
        await _context.SaveChangesAsync();

        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == _incumbent.Id)).IsDeleted
            .ShouldBeTrue("a slot the planner could not fill is emptied");
    }

    [Test]
    public async Task BlockedSlot_IncumbentWithExpensesAndNoTarget_StaysWithItsChildren()
    {
        await AddIncumbentExpensesAsync();
        var pause = new Break
        {
            Id = Guid.NewGuid(), ClientId = AgentA, CurrentDate = Day, ParentWorkId = _incumbent.Id,
            StartTime = new TimeOnly(12, 0), EndTime = new TimeOnly(12, 30), AnalyseToken = _token,
        };
        _context.Break.Add(pause);
        await _context.SaveChangesAsync();
        var displaced = await DisplaceAsync();

        var changedEmployee = await _service.ResolveDisplacedWorksAsync(_token, displaced, [], CancellationToken.None);
        await _context.SaveChangesAsync();

        changedEmployee.ShouldBe(0);
        (await _context.Work.SingleAsync(w => w.Id == _incumbent.Id)).ClientId.ShouldBe(AgentA);
        (await _context.Expenses.CountAsync(e => !e.IsDeleted && e.WorkId == _incumbent.Id)).ShouldBe(2);
        (await _context.Break.IgnoreQueryFilters().SingleAsync(b => b.Id == pause.Id)).IsDeleted.ShouldBeFalse();
    }

    [Test]
    public async Task BlockedSlot_EmployeeBookedElsewhereThatDay_IsEmptied_ExpensesFollowTheNewWork()
    {
        await AddIncumbentExpensesAsync();
        var displaced = await DisplaceAsync();
        var elsewhere = await AddCreatedWorkAsync(AgentA, Guid.NewGuid());

        var changedEmployee = await _service.ResolveDisplacedWorksAsync(_token, displaced, [elsewhere.Id], CancellationToken.None);
        await _context.SaveChangesAsync();

        changedEmployee.ShouldBe(0);
        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == _incumbent.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Expenses.CountAsync(e => !e.IsDeleted && e.WorkId == elsewhere.Id)).ShouldBe(2);
        (await _context.Work.SingleAsync(w => w.Id == elsewhere.Id)).ClonedFromWorkId
            .ShouldBeNull("a work on another shift is not a successor of the displaced work's origin");
    }

    [Test]
    public async Task QuantityTwo_EachDisplacedEmployeeKeepsTheirOwnNewWork_ExpensesAreNeverStacked()
    {
        var incumbentC = ReplaceSetUpIncumbentWith(AgentC, AgentA);
        var incumbentA = _incumbent;
        await AddExpenseAsync(incumbentC.Id);
        await AddExpenseAsync(incumbentA.Id);
        var displaced = await DisplaceSlotAsync(2);
        var ownWorkOfA = await AddCreatedWorkAsync(AgentA, _shiftId);
        var workOfB = await AddCreatedWorkAsync(AgentB, _shiftId);

        var changedEmployee = await _service.ResolveDisplacedWorksAsync(
            _token, displaced, [ownWorkOfA.Id, workOfB.Id], CancellationToken.None);
        await _context.SaveChangesAsync();

        (await ExpenseCountOf(ownWorkOfA.Id)).ShouldBe(1, "A's expense stays with A's new work");
        (await ExpenseCountOf(workOfB.Id)).ShouldBe(1, "C's expense goes to the remaining slot work, not onto A's");
        changedEmployee.ShouldBe(1);
        (await _context.Work.SingleAsync(w => w.Id == ownWorkOfA.Id)).ClonedFromWorkId.ShouldBe(incumbentA.ClonedFromWorkId);
        (await _context.Work.SingleAsync(w => w.Id == workOfB.Id)).ClonedFromWorkId.ShouldBe(incumbentC.ClonedFromWorkId);
    }

    [Test]
    public async Task QuantityThree_SameClientFirst_ThenOneToOne()
    {
        var incumbentB = ReplaceSetUpIncumbentWith(AgentB, AgentC, AgentA);
        await AddExpenseAsync(incumbentB[0].Id);
        await AddExpenseAsync(incumbentB[1].Id);
        await AddExpenseAsync(_incumbent.Id);
        var displaced = await DisplaceSlotAsync(3);
        var ownWorkOfA = await AddCreatedWorkAsync(AgentA, _shiftId);
        var workOfD = await AddCreatedWorkAsync(AgentD, _shiftId);
        var workOfE = await AddCreatedWorkAsync(AgentE, _shiftId);

        await _service.ResolveDisplacedWorksAsync(
            _token, displaced, [ownWorkOfA.Id, workOfD.Id, workOfE.Id], CancellationToken.None);
        await _context.SaveChangesAsync();

        (await ExpenseCountOf(ownWorkOfA.Id)).ShouldBe(1);
        (await ExpenseCountOf(workOfD.Id)).ShouldBe(1);
        (await ExpenseCountOf(workOfE.Id)).ShouldBe(1);
    }

    [Test]
    public async Task QuantityThree_TwoNewWorks_LeftoverIsDistributed_NoWorkCarriesAll()
    {
        foreach (var incumbent in ReplaceSetUpIncumbentWith(AgentB, AgentC, AgentA))
        {
            await AddExpenseAsync(incumbent.Id);
        }

        var displaced = await DisplaceSlotAsync(3);
        var workOfD = await AddCreatedWorkAsync(AgentD, _shiftId);
        var workOfE = await AddCreatedWorkAsync(AgentE, _shiftId);

        await _service.ResolveDisplacedWorksAsync(_token, displaced, [workOfD.Id, workOfE.Id], CancellationToken.None);
        await _context.SaveChangesAsync();

        var counts = new[] { await ExpenseCountOf(workOfD.Id), await ExpenseCountOf(workOfE.Id) };
        counts.Sum().ShouldBe(3, "no expense is lost");
        counts.ShouldAllBe(c => c >= 1, "every new work of the slot takes one displaced work before any takes a second");
    }

    [Test]
    public async Task CorrectedWorkBlocksMutation_IsNeverDisplaced()
    {
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(), WorkId = _incumbent.Id, Type = WorkChangeType.CorrectionEnd,
            StartTime = new TimeOnly(16, 0), EndTime = new TimeOnly(17, 0), ChangeTime = 1m, AnalyseToken = _token,
        });
        await _context.SaveChangesAsync();

        var displaced = await _service.SoftDeleteClonedWorksOnSlotsAsync(
            _token, Day, Day, new HashSet<(Guid, DateOnly)> { (_shiftId, Day) }, CancellationToken.None);
        await _context.SaveChangesAsync();

        displaced.ShouldBeEmpty();
        (await _context.Work.SingleAsync(w => w.Id == _incumbent.Id)).IsDeleted.ShouldBeFalse();
        (await _context.WorkChange.SingleAsync(wc => wc.WorkId == _incumbent.Id)).IsDeleted.ShouldBeFalse();
    }

    /// <summary>
    /// Replaces the set-up incumbent by incumbents of the given agents on the same slot, inserted in that order (the last
    /// one becomes <see cref="_incumbent"/>), each with its own clone origin. Inserting another employee before the
    /// employee who also has a new work on the slot makes an order-dependent successor choice visible.
    /// </summary>
    private Work[] ReplaceSetUpIncumbentWith(params Guid[] agents)
    {
        _context.Work.Remove(_incumbent);
        var works = agents.Select(agent =>
        {
            var work = NewWork(agent);
            work.ClonedFromWorkId = Guid.NewGuid();
            _context.Work.Add(work);
            return work;
        }).ToArray();
        _incumbent = works[^1];
        return works;
    }

    private Work ReplaceSetUpIncumbentWith(Guid firstAgent, Guid lastAgent) => ReplaceSetUpIncumbentWith(new[] { firstAgent, lastAgent })[0];

    private async Task AddExpenseAsync(Guid workId)
    {
        _context.Expenses.Add(NewExpense(workId, taxable: false));
        await _context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<Guid>> DisplaceSlotAsync(int expectedCount)
    {
        await _context.SaveChangesAsync();
        var displaced = await _service.SoftDeleteClonedWorksOnSlotsAsync(
            _token, Day, Day, new HashSet<(Guid, DateOnly)> { (_shiftId, Day) }, CancellationToken.None);
        await _context.SaveChangesAsync();
        displaced.Count.ShouldBe(expectedCount);
        return displaced;
    }

    private Task<int> ExpenseCountOf(Guid workId) => _context.Expenses.CountAsync(e => !e.IsDeleted && e.WorkId == workId);

    private async Task AddIncumbentExpensesAsync()
    {
        _context.Expenses.Add(NewExpense(_incumbent.Id, taxable: false));
        _context.Expenses.Add(NewExpense(_incumbent.Id, taxable: true));
        await _context.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<Guid>> DisplaceAsync()
    {
        var expensesBefore = await _context.Expenses.CountAsync(e => !e.IsDeleted && e.WorkId == _incumbent.Id);
        var displaced = await _service.SoftDeleteClonedWorksOnSlotsAsync(
            _token, Day, Day, new HashSet<(Guid, DateOnly)> { (_shiftId, Day) }, CancellationToken.None);
        await _context.SaveChangesAsync();
        displaced.ShouldBe(new[] { _incumbent.Id });
        (await _context.Expenses.CountAsync(e => !e.IsDeleted && e.WorkId == _incumbent.Id))
            .ShouldBe(expensesBefore, "expenses are never deleted with the displaced work");
        return displaced;
    }

    private async Task<Work> AddCreatedWorkAsync(Guid clientId, Guid shiftId)
    {
        var work = NewWork(clientId);
        work.ShiftId = shiftId;
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work;
    }

    private Work NewWork(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = _shiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        WorkTime = 8m,
        AnalyseToken = _token,
    };

    private Expenses NewExpense(Guid workId, bool taxable) => new()
    {
        Id = Guid.NewGuid(), WorkId = workId, Amount = taxable ? 40m : 12.5m, Taxable = taxable, AnalyseToken = _token,
    };
}
