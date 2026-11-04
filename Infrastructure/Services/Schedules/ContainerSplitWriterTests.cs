// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// What a container split stages: the container ends at the split time, the second part is a new container with the bases
/// of the first and nothing from the template, and the moved children keep their ids (nothing is soft-deleted). Moved
/// services without customer go to the person of the second part, services with customer stay with the customer, moved
/// absences take the person of the second part; moved children take the day of the second part (night split).
/// </summary>

using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ContainerSplitWriterTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);

    private DataBaseContext _context = null!;
    private Guid _containerId;
    private Guid _personA;
    private Guid _personB;
    private Guid _customer;
    private Guid _ownTaskShift;
    private Guid _customerTaskShift;

    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _containerId = Guid.NewGuid();
        _personA = Guid.NewGuid();
        _personB = Guid.NewGuid();
        _customer = Guid.NewGuid();
        _ownTaskShift = Guid.NewGuid();
        _customerTaskShift = Guid.NewGuid();
        _context.Shift.AddRange(
            new Shift { Id = _ownTaskShift, Name = "own", ClientId = null },
            new Shift { Id = _customerTaskShift, Name = "customer", ClientId = _customer });
        _context.Work.Add(new Work
        {
            Id = _containerId, ClientId = _personA, ShiftId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = T(22), EndTime = T(6), StartBase = "Depot A", EndBase = "Depot B",
        });
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private Work Child(Guid shiftId, Guid clientId, TimeOnly start, TimeOnly end) => new()
    {
        Id = Guid.NewGuid(), ClientId = clientId, ShiftId = shiftId, CurrentDate = Day, StartTime = start, EndTime = end,
        ParentWorkId = _containerId,
    };

    [Test]
    public async Task Split_ReparentsMovedChildrenWithTheirIds_AndDeletesNothing()
    {
        var evening = Child(_ownTaskShift, _personA, T(22, 30), T(1));
        var ownMorning = Child(_ownTaskShift, _personA, T(3), T(4));
        var customerMorning = Child(_customerTaskShift, _customer, T(4), T(5));
        var absence = new Break
        {
            Id = Guid.NewGuid(), ClientId = _personA, AbsenceId = Guid.NewGuid(), CurrentDate = Day,
            StartTime = T(5), EndTime = T(5, 30), ParentWorkId = _containerId,
        };
        _context.Work.AddRange(evening, ownMorning, customerMorning);
        _context.Break.Add(absence);
        await _context.SaveChangesAsync();

        var second = await new ContainerSplitWriter(_context).StageAsync(
            new ContainerSplitPlan(_containerId, T(2), Day.AddDays(1), _personB,
                new HashSet<Guid> { ownMorning.Id, customerMorning.Id, absence.Id }),
            CancellationToken.None);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var first = await _context.Work.SingleAsync(w => w.Id == _containerId);
        first.EndTime.ShouldBe(T(2));
        first.StartBase.ShouldBe("Depot A");

        var stored = await _context.Work.SingleAsync(w => w.Id == second.Id);
        stored.ClientId.ShouldBe(_personB);
        stored.CurrentDate.ShouldBe(Day.AddDays(1));
        (stored.StartTime, stored.EndTime).ShouldBe((T(2), T(6)));
        (stored.StartBase, stored.EndBase).ShouldBe(("Depot A", "Depot B"));

        var own = await _context.Work.SingleAsync(w => w.Id == ownMorning.Id);
        (own.ParentWorkId, own.ClientId, own.CurrentDate).ShouldBe((second.Id, _personB, Day.AddDays(1)));
        var withCustomer = await _context.Work.SingleAsync(w => w.Id == customerMorning.Id);
        (withCustomer.ParentWorkId, withCustomer.ClientId).ShouldBe((second.Id, _customer));
        var movedBreak = await _context.Break.SingleAsync(b => b.Id == absence.Id);
        (movedBreak.ParentWorkId, movedBreak.ClientId, movedBreak.CurrentDate).ShouldBe((second.Id, _personB, Day.AddDays(1)));
        (await _context.Work.SingleAsync(w => w.Id == evening.Id)).ParentWorkId.ShouldBe(_containerId);

        (await _context.Work.IgnoreQueryFilters().CountAsync(w => w.IsDeleted)).ShouldBe(0);
        (await _context.Break.IgnoreQueryFilters().CountAsync(b => b.IsDeleted)).ShouldBe(0);
        (await _context.Work.CountAsync(w => w.ParentWorkId == second.Id)).ShouldBe(2);
        (await _context.Expenses.CountAsync()).ShouldBe(0);
    }
}
