// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// EntityCollectionUpdateService.UpdateCollection on a tracked parent: a new child is added to the parent's navigation
/// exactly once. Marking the child Added already lets EF fix it up into the tracked navigation, so a second explicit Add
/// put the same instance in the list twice - invisible to SaveChanges, but a later reader of the tracked aggregate in the
/// same context (sealing clones the order's expenses into the plannable shift) saw two rows (found 2026-10-25 by the order
/// review IT: the plannable shift and every cut part got the expense twice).
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class EntityCollectionUpdateServiceTrackedParentTests
{
    [Test]
    public async Task ANewChild_OfATrackedParent_IsInTheNavigationOnce()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        var shift = new Shift { Id = Guid.NewGuid(), Name = "Order", Abbreviation = "O" };
        context.Shift.Add(shift);
        await context.SaveChangesAsync();
        var tracked = await context.Shift.Include(s => s.ShiftExpenses).SingleAsync(s => s.Id == shift.Id);

        var incoming = new List<ShiftExpenses> { new() { Id = Guid.NewGuid(), Description = "Meal", Amount = 15m } };
        new EntityCollectionUpdateService(context).UpdateCollection(
            tracked.ShiftExpenses, incoming, tracked.Id, (expense, shiftId) => expense.ShiftId = shiftId);

        Assert.That(tracked.ShiftExpenses, Has.Count.EqualTo(1));
        await context.SaveChangesAsync();
        Assert.That(tracked.ShiftExpenses, Has.Count.EqualTo(1));
    }
}
