// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ProactiveTriggerDispatchRepository: RecordAsync persists a dispatch row once per
/// (user, kind, dedup key), and after a failed SaveChangesAsync the poisoned row is detached so
/// the change tracker stays clean and subsequent records in the same scope still succeed.
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
