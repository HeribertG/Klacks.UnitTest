// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Scheduling;
using Microsoft.EntityFrameworkCore;
using Work = Klacks.Api.Domain.Models.Schedules.Work;

namespace Klacks.UnitTest.Infrastructure.Repositories.Scheduling;

/// <summary>
/// A container sub-work (ParentWorkId set) is part of its container, not a shift of its own: a container 07-19 with
/// a task from 15:00 would otherwise read as an early AND a late shift on that day and fake a Late->Early finding.
/// </summary>
[TestFixture]
public class PlanningRuleDataReaderTests
{
    [Test]
    public async Task GetWorkSpansAsync_LeavesContainerSubWorksOut()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new DataBaseContext(options, null!);
        var clientId = Guid.NewGuid();
        var day = new DateOnly(2026, 7, 14);
        var container = NewWork(clientId, day, new TimeOnly(7, 0), new TimeOnly(19, 0), parentWorkId: null);
        context.Work.Add(container);
        context.Work.Add(NewWork(clientId, day, new TimeOnly(15, 0), new TimeOnly(19, 0), container.Id));
        await context.SaveChangesAsync();

        var spans = await new PlanningRuleDataReader(context).GetWorkSpansAsync([clientId], day, day, null);

        spans.ShouldHaveSingleItem().WorkId.ShouldBe(container.Id);
    }

    private static Work NewWork(Guid clientId, DateOnly date, TimeOnly start, TimeOnly end, Guid? parentWorkId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ShiftId = Guid.NewGuid(),
        CurrentDate = date,
        StartTime = start,
        EndTime = end,
        WorkTime = 4m,
        ParentWorkId = parentWorkId,
    };
}
