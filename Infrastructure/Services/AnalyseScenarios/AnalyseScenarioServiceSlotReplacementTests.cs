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
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceSlotReplacementTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

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
        _service = new AnalyseScenarioService(_context);

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
