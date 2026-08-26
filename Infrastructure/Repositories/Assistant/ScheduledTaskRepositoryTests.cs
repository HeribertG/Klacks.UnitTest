// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ScheduledTaskRepository's due scan. It must return only tasks that are enabled, NOT paused
/// and whose next run has passed. The paused case is the one worth pinning: pausing is what the runner
/// does instead of disabling when an irreversible skill lacks its opt-in, so IsEnabled deliberately stays
/// true - if the scan ignored IsPaused the refused task would be retried on every single tick.
/// Uses a shared in-memory DataBaseContext, mirroring the neighbouring repository tests.
/// TryClaimAsync uses ExecuteUpdateAsync, which the EF in-memory provider does not support, so it is
/// intentionally not covered here (same as the other ExecuteUpdateAsync repositories).
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class ScheduledTaskRepositoryTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static ScheduledTask Task(string name, bool isEnabled = true, bool isPaused = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CronExpression = "0 8 * * 1",
        TimeZoneId = "Europe/Zurich",
        ActionType = ScheduledTaskActionTypes.Reminder,
        MessageText = "check coverage",
        OwnerUserId = Guid.NewGuid(),
        OwnerUserName = "alice",
        IsEnabled = isEnabled,
        IsPaused = isPaused,
        NextRunUtc = NowUtc.AddMinutes(-1)
    };

    private async System.Threading.Tasks.Task SeedAsync(params ScheduledTask[] tasks)
    {
        await using var context = CreateContext();
        await context.ScheduledTasks.AddRangeAsync(tasks);
        await context.SaveChangesAsync();
    }

    [Test]
    public async System.Threading.Tasks.Task GetDueAsync_SkipsAPausedTask_EvenWhileItIsStillEnabled()
    {
        var paused = Task("paused task", isPaused: true);
        var running = Task("running task");
        await SeedAsync(paused, running);

        await using var context = CreateContext();
        var repository = new ScheduledTaskRepository(context);

        var due = await repository.GetDueAsync(NowUtc);

        due.Select(t => t.Id).ShouldBe(new[] { running.Id });
        (await context.ScheduledTasks.FindAsync(paused.Id))!.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async System.Threading.Tasks.Task GetDueAsync_SkipsADisabledTask()
    {
        var disabled = Task("disabled task", isEnabled: false);
        var running = Task("running task");
        await SeedAsync(disabled, running);

        await using var context = CreateContext();
        var repository = new ScheduledTaskRepository(context);

        var due = await repository.GetDueAsync(NowUtc);

        due.Select(t => t.Id).ShouldBe(new[] { running.Id });
    }

    [Test]
    public async System.Threading.Tasks.Task GetDueAsync_SkipsATaskWhoseNextRunHasNotArrived()
    {
        var later = Task("later task");
        later.NextRunUtc = NowUtc.AddMinutes(1);
        var running = Task("running task");
        await SeedAsync(later, running);

        await using var context = CreateContext();
        var repository = new ScheduledTaskRepository(context);

        var due = await repository.GetDueAsync(NowUtc);

        due.Select(t => t.Id).ShouldBe(new[] { running.Id });
    }
}
