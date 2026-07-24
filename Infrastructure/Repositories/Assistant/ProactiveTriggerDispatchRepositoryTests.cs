// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProactiveTriggerDispatchRepository: RecordAsync persists a dispatch row once per
/// (user, kind, dedup key), and after a failed SaveChangesAsync the poisoned row is detached so
/// the change tracker stays clean and subsequent records in the same scope still succeed.
/// Also covers the inbox surface: listing own rows newest first with unread filter and take,
/// counting unread rows, and marking single rows (ownership enforced) or all rows as read.
/// Uses a shared in-memory DataBaseContext, mirroring the neighbouring repository tests.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class ProactiveTriggerDispatchRepositoryTests
{
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

    private static ProactiveTriggerDispatchRow Row(Guid id, string userId, string dedupKey) => new()
    {
        Id = id,
        UserId = userId,
        TriggerKind = "test_kind",
        DedupKey = dedupKey,
        ContentKey = "content",
        Severity = "low"
    };

    [Test]
    public async Task RecordAsync_PersistsRow()
    {
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context);

        await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task RecordAsync_SameUserKindAndDedupKey_IsRecordedOnlyOnce()
    {
        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context);

        await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
        await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task ListForUserAsync_ReturnsOnlyOwnRows_NewestFirst()
    {
        var oldestId = Guid.NewGuid();
        var newestId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext);
            await repository.RecordAsync(Row(oldestId, "user-a", "dedup-1"));
            await Task.Delay(TimeSpan.FromMilliseconds(10));
            await repository.RecordAsync(Row(newestId, "user-a", "dedup-2"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-b", "dedup-3"));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context)
            .ListForUserAsync("user-a", unreadOnly: false, take: 10);

        result.Count.ShouldBe(2);
        result[0].Id.ShouldBe(newestId);
        result[1].Id.ShouldBe(oldestId);
    }

    [Test]
    public async Task ListForUserAsync_UnreadOnly_ExcludesReadRows()
    {
        var readId = Guid.NewGuid();
        var unreadId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext);
            await repository.RecordAsync(Row(readId, "user-a", "dedup-1"));
            await repository.RecordAsync(Row(unreadId, "user-a", "dedup-2"));
            await repository.MarkReadAsync(readId, "user-a");
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context)
            .ListForUserAsync("user-a", unreadOnly: true, take: 10);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(unreadId);
    }

    [Test]
    public async Task ListForUserAsync_RespectsTake()
    {
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext);
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-2"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-3"));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context)
            .ListForUserAsync("user-a", unreadOnly: false, take: 2);

        result.Count.ShouldBe(2);
    }

    [Test]
    public async Task CountUnreadAsync_CountsOnlyOwnUnreadRows()
    {
        var readId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext);
            await repository.RecordAsync(Row(readId, "user-a", "dedup-1"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-2"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-b", "dedup-3"));
            await repository.MarkReadAsync(readId, "user-a");
        }

        using var context = CreateContext();
        var count = await new ProactiveTriggerDispatchRepository(context).CountUnreadAsync("user-a");

        count.ShouldBe(1);
    }

    [Test]
    public async Task MarkReadAsync_OwnUnreadRow_SetsReadAtUtcAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext).RecordAsync(Row(id, "user-a", "dedup-1"));
        }

        var before = DateTime.UtcNow;
        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context).MarkReadAsync(id, "user-a");

        result.ShouldBeTrue();
        using var verify = CreateContext();
        var row = await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id);
        row.ReadAtUtc.ShouldNotBeNull();
        row.ReadAtUtc.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task MarkReadAsync_AlreadyReadRow_KeepsOriginalTimestampAndReturnsTrue()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext);
            await repository.RecordAsync(Row(id, "user-a", "dedup-1"));
            await repository.MarkReadAsync(id, "user-a");
        }

        DateTime? firstReadAt;
        using (var verifyFirst = CreateContext())
        {
            firstReadAt = (await verifyFirst.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(10));
        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context).MarkReadAsync(id, "user-a");

        result.ShouldBeTrue();
        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc.ShouldBe(firstReadAt);
    }

    [Test]
    public async Task MarkReadAsync_RowOfOtherUser_ReturnsFalseAndDoesNotMark()
    {
        var id = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext).RecordAsync(Row(id, "user-b", "dedup-1"));
        }

        using var context = CreateContext();
        var result = await new ProactiveTriggerDispatchRepository(context).MarkReadAsync(id, "user-a");

        result.ShouldBeFalse();
        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == id)).ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task MarkReadAsync_UnknownId_ReturnsFalse()
    {
        using var context = CreateContext();

        var result = await new ProactiveTriggerDispatchRepository(context).MarkReadAsync(Guid.NewGuid(), "user-a");

        result.ShouldBeFalse();
    }

    [Test]
    public async Task MarkAllReadAsync_MarksAllOwnUnreadRows_LeavesOtherUsersUntouched()
    {
        var foreignId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            var repository = new ProactiveTriggerDispatchRepository(seedContext);
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-1"));
            await repository.RecordAsync(Row(Guid.NewGuid(), "user-a", "dedup-2"));
            await repository.RecordAsync(Row(foreignId, "user-b", "dedup-3"));
        }

        using var context = CreateContext();
        await new ProactiveTriggerDispatchRepository(context).MarkAllReadAsync("user-a");

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync(d => d.UserId == "user-a" && d.ReadAtUtc == null)).ShouldBe(0);
        (await verify.AgentTriggerDispatches.SingleAsync(d => d.Id == foreignId)).ReadAtUtc.ShouldBeNull();
    }

    [Test]
    public async Task RecordAsync_AfterFailedSave_LeavesTrackerCleanAndNextRecordSucceeds()
    {
        var duplicateId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            await new ProactiveTriggerDispatchRepository(seedContext).RecordAsync(Row(duplicateId, "user-a", "dedup-1"));
        }

        using var context = CreateContext();
        var repository = new ProactiveTriggerDispatchRepository(context);

        await Should.ThrowAsync<Exception>(
            () => repository.RecordAsync(Row(duplicateId, "user-b", "dedup-2")));

        context.ChangeTracker.Entries<ProactiveTriggerDispatchRow>()
            .Count(entry => entry.State == EntityState.Added)
            .ShouldBe(0);

        await repository.RecordAsync(Row(Guid.NewGuid(), "user-c", "dedup-3"));

        using var verify = CreateContext();
        (await verify.AgentTriggerDispatches.CountAsync()).ShouldBe(2);
    }
}
