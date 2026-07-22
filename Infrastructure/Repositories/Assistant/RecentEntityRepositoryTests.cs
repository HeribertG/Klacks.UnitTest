// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RecentEntityRepository: Add fills id/timestamp when unset, keeps the per-conversation ring
/// bounded by hard-deleting the oldest rows beyond the retention bound, isolates rows by user and
/// conversation, and returns retained rows newest-first. Uses a shared in-memory DataBaseContext,
/// mirroring the pending-store repository tests.
/// </summary>

using Klacks.Api.Domain.Constants;
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
public class RecentEntityRepositoryTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    private static readonly Guid User1 = Guid.NewGuid();
    private static readonly Guid User2 = Guid.NewGuid();
    private const string Conversation1 = "conversation-1";
    private const string Conversation2 = "conversation-2";

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private RecentEntityRepository CreateRepository() => new(CreateContext());

    private static RecentEntityRow Row(Guid userId, string conversationId, DateTime createdAtUtc) => new()
    {
        UserId = userId,
        ConversationId = conversationId,
        EntityType = "shift",
        EntityId = Guid.NewGuid(),
        DisplayName = "Sample",
        Action = RecentEntityDefaults.ActionCreated,
        CreatedAtUtc = createdAtUtc
    };

    [Test]
    public async Task AddAsync_fills_id_and_timestamp_when_unset()
    {
        var row = new RecentEntityRow
        {
            UserId = User1,
            ConversationId = Conversation1,
            EntityType = "group",
            EntityId = Guid.NewGuid(),
            Action = RecentEntityDefaults.ActionCreated
        };

        await CreateRepository().AddAsync(row);

        using var verify = CreateContext();
        var stored = verify.RecentEntities.Single();
        stored.Id.ShouldNotBe(Guid.Empty);
        stored.CreatedAtUtc.ShouldNotBe(default);
    }

    [Test]
    public async Task AddAsync_bounds_the_ring_and_hard_deletes_oldest()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var overflow = RecentEntityDefaults.MaxPerConversation + 2;

        for (var i = 0; i < overflow; i++)
        {
            await CreateRepository().AddAsync(Row(User1, Conversation1, baseTime.AddMinutes(i)));
        }

        using var verify = CreateContext();
        verify.RecentEntities.Count(r => r.UserId == User1 && r.ConversationId == Conversation1)
            .ShouldBe(RecentEntityDefaults.MaxPerConversation);

        var oldestKept = verify.RecentEntities
            .Where(r => r.UserId == User1 && r.ConversationId == Conversation1)
            .Min(r => r.CreatedAtUtc);
        oldestKept.ShouldBe(baseTime.AddMinutes(2));
    }

    [Test]
    public async Task GetRecentAsync_isolates_by_user_and_conversation()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await CreateRepository().AddAsync(Row(User1, Conversation1, t));
        await CreateRepository().AddAsync(Row(User1, Conversation2, t));
        await CreateRepository().AddAsync(Row(User2, Conversation1, t));

        (await CreateRepository().GetRecentAsync(User1, Conversation1)).Count.ShouldBe(1);
        (await CreateRepository().GetRecentAsync(User1, Conversation2)).Count.ShouldBe(1);
        (await CreateRepository().GetRecentAsync(User2, Conversation1)).Count.ShouldBe(1);
        (await CreateRepository().GetRecentAsync(User2, Conversation2)).Count.ShouldBe(0);
    }

    [Test]
    public async Task GetRecentAsync_returns_newest_first()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldest = Row(User1, Conversation1, baseTime);
        var newest = Row(User1, Conversation1, baseTime.AddMinutes(5));
        await CreateRepository().AddAsync(oldest);
        await CreateRepository().AddAsync(newest);

        var recent = await CreateRepository().GetRecentAsync(User1, Conversation1);

        recent.Count.ShouldBe(2);
        recent[0].EntityId.ShouldBe(newest.EntityId);
        recent[1].EntityId.ShouldBe(oldest.EntityId);
    }
}
