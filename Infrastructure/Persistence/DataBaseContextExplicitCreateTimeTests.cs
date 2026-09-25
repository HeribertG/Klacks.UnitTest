// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The context stamps every added BaseEntity with the save time, except an entity that carries the
/// IKeepsExplicitCreateTime marker and has a CreateTime of its own. The one such entity today is the chat
/// history row: a stopped turn persisted after the user moved on stamps its rows with the time the turn began,
/// and without this exception the context overwrote that with the save time, so the old request read as the
/// latest message (found by the Postgres integration test of the supersede scenario; the unit tests of the
/// recorder use a substituted repository and could not see it). Rows without the marker are stamped as before,
/// whatever CreateTime they carry.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Persistence;

[TestFixture]
public class DataBaseContextExplicitCreateTimeTests
{
    private static readonly DateTime EarlierThanNow = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private LLMConversation _conversation = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _conversation = new LLMConversation { Id = Guid.NewGuid(), ConversationId = "conv", UserId = "user" };
        _context.Set<LLMConversation>().Add(_conversation);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task AHistoryRowWithAnExplicitCreateTime_KeepsIt()
    {
        var message = Message(EarlierThanNow);
        _context.Set<LLMMessage>().Add(message);

        await _context.SaveChangesAsync();

        message.CreateTime.ShouldBe(EarlierThanNow);
    }

    [Test]
    public async Task AHistoryRowWithoutACreateTime_IsStampedWithTheSaveTime()
    {
        var message = Message(null);
        var before = DateTime.UtcNow;
        _context.Set<LLMMessage>().Add(message);

        await _context.SaveChangesAsync();

        message.CreateTime.ShouldNotBeNull();
        message.CreateTime.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task ARowWithoutTheMarker_IsStampedWithTheSaveTimeWhateverItCarries()
    {
        var conversation = new LLMConversation
        {
            Id = Guid.NewGuid(),
            ConversationId = "other",
            UserId = "user",
            CreateTime = EarlierThanNow
        };
        var before = DateTime.UtcNow;
        _context.Set<LLMConversation>().Add(conversation);

        await _context.SaveChangesAsync();

        conversation.CreateTime!.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task ChangingTheHistoryRowLater_StillStampsItsUpdateTimeAndKeepsItsCreateTime()
    {
        var message = Message(EarlierThanNow);
        _context.Set<LLMMessage>().Add(message);
        await _context.SaveChangesAsync();

        message.Content = "changed";
        await _context.SaveChangesAsync();

        message.CreateTime.ShouldBe(EarlierThanNow);
        message.UpdateTime.ShouldNotBeNull();
    }

    private LLMMessage Message(DateTime? createTime) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = _conversation.Id,
        Role = "user",
        Content = "text",
        CreateTime = createTime
    };
}
