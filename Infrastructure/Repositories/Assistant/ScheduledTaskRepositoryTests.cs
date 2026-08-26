// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ScheduledTaskRepository's due scan and owner listing. The due scan must return only tasks
/// that are enabled, NOT paused and whose next run has passed; the owner listing must apply the same
/// "will actually fire" definition when disabled tasks are excluded. The paused case is the one worth
/// pinning: pausing is what the runner does instead of disabling for a cause the owner can fix, so
/// IsEnabled deliberately stays true - a query filtering on IsEnabled alone would retry the refused task
/// on every tick and present it to its owner as running.
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

    private static ScheduledTask Task(string name, bool isEnabled = true, bool isPaused = false, Guid? ownerUserId = null)
    {
        var task = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            CronExpression = "0 8 * * 1",
            TimeZoneId = "Europe/Zurich",
            ActionType = ScheduledTaskActionTypes.Reminder,
            MessageText = "check coverage",
            OwnerUserId = ownerUserId ?? Guid.NewGuid(),
            OwnerUserName = "alice",
            IsEnabled = isEnabled,
            NextRunUtc = NowUtc.AddMinutes(-1)
        };

        if (isPaused)
        {
            task.Pause("refused by the unattended policy");
        }

        return task;
    }

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
    public async System.Threading.Tasks.Task GetByOwnerAsync_WithoutDisabled_AlsoHidesAPausedTask()
    {
        var owner = Guid.NewGuid();
        var paused = Task("paused task", isPaused: true, ownerUserId: owner);
        var disabled = Task("disabled task", isEnabled: false, ownerUserId: owner);
        var running = Task("running task", ownerUserId: owner);
        await SeedAsync(paused, disabled, running);

        await using var context = CreateContext();
        var repository = new ScheduledTaskRepository(context);

        var tasks = await repository.GetByOwnerAsync(owner, includeDisabled: false);

        tasks.Select(t => t.Id).ShouldBe(new[] { running.Id });
    }

    [Test]
    public async System.Threading.Tasks.Task GetByOwnerAsync_IncludingDisabled_StillReportsThePausedTaskAndItsReason()
    {
        var owner = Guid.NewGuid();
        var paused = Task("paused task", isPaused: true, ownerUserId: owner);
        await SeedAsync(paused);

        await using var context = CreateContext();
        var repository = new ScheduledTaskRepository(context);

        var tasks = await repository.GetByOwnerAsync(owner, includeDisabled: true);

        var loaded = tasks.ShouldHaveSingleItem();
        loaded.IsPaused.ShouldBeTrue();
        loaded.IsEnabled.ShouldBeTrue();
        loaded.PausedReason.ShouldBe("refused by the unattended policy");
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
