// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression tests for the conversation IDOR: every LLMConversation lookup must be scoped to the
/// owning user, so a second user can neither read another user's conversation nor have their own
/// messages land in it. Uses a shared in-memory DataBaseContext, mirroring the other assistant
/// repository tests.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class LLMRepositoryConversationOwnershipTests
{
    private const string SharedConversationId = "conversation-1";
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const string UserRole = "user";
    private const int TotalTokensOfA = 4321;

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

    private LLMRepository CreateRepository() =>
        new(CreateContext(), Substitute.For<ILogger<LLMModel>>());

    private async Task<Guid> SeedConversationOfUserAAsync(int messageCount = 2)
    {
        await using var context = CreateContext();
        var conversation = new LLMConversation
        {
            Id = Guid.NewGuid(),
            ConversationId = SharedConversationId,
            UserId = UserA,
            MessageCount = messageCount,
            TotalTokens = TotalTokensOfA,
            LastMessageAt = DateTime.UtcNow
        };
        context.Set<LLMConversation>().Add(conversation);

        for (var i = 0; i < messageCount; i++)
        {
            context.Set<LLMMessage>().Add(new LLMMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Role = UserRole,
                Content = $"secret of A {i}",
                CreateTime = DateTime.UtcNow.AddMinutes(i)
            });
        }

        await context.SaveChangesAsync();
        return conversation.Id;
    }

    [Test]
    public async Task GetConversationByConversationIdAsync_ForeignUser_GetsNothing()
    {
        await SeedConversationOfUserAAsync();

        var asOwner = await CreateRepository().GetConversationByConversationIdAsync(SharedConversationId, UserA);
        var asIntruder = await CreateRepository().GetConversationByConversationIdAsync(SharedConversationId, UserB);

        asOwner.ShouldNotBeNull();
        asIntruder.ShouldBeNull();
    }

    [Test]
    public async Task GetConversationMessagesAsync_ForeignUser_GetsEmptyList()
    {
        await SeedConversationOfUserAAsync();

        var asOwner = await CreateRepository().GetConversationMessagesAsync(SharedConversationId, UserA);
        var asIntruder = await CreateRepository().GetConversationMessagesAsync(SharedConversationId, UserB);

        asOwner.Count.ShouldBe(2);
        asIntruder.ShouldBeEmpty();
    }

    [Test]
    public async Task GetOldestMessagesAsync_ForeignUser_GetsEmptyList()
    {
        await SeedConversationOfUserAAsync(messageCount: 6);

        var asOwner = await CreateRepository().GetOldestMessagesAsync(SharedConversationId, UserA, skipNewest: 2);
        var asIntruder = await CreateRepository().GetOldestMessagesAsync(SharedConversationId, UserB, skipNewest: 2);

        asOwner.Count.ShouldBe(4);
        asIntruder.ShouldBeEmpty();
    }

    [Test]
    public async Task GetConversationTokenCountAsync_ForeignUser_GetsZero()
    {
        await SeedConversationOfUserAAsync();

        var asOwner = await CreateRepository().GetConversationTokenCountAsync(SharedConversationId, UserA);
        var asIntruder = await CreateRepository().GetConversationTokenCountAsync(SharedConversationId, UserB);

        asOwner.ShouldBe(TotalTokensOfA);
        asIntruder.ShouldBe(0);
    }

    [Test]
    public async Task GetOrCreateConversationAsync_ForeignUser_GetsOwnRowInsteadOfHijackingTheExistingOne()
    {
        var conversationIdOfA = await SeedConversationOfUserAAsync();

        var forUserB = await CreateRepository().GetOrCreateConversationAsync(SharedConversationId, UserB);

        forUserB.Id.ShouldNotBe(conversationIdOfA);
        forUserB.UserId.ShouldBe(UserB);
        forUserB.MessageCount.ShouldBe(0);
    }

    [Test]
    public async Task SavedMessageOfForeignUser_DoesNotLandInTheOtherUsersConversation()
    {
        var conversationIdOfA = await SeedConversationOfUserAAsync();

        var repository = CreateRepository();
        var conversationOfB = await repository.GetOrCreateConversationAsync(SharedConversationId, UserB);
        await repository.SaveMessageAsync(new LLMMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationOfB.Id,
            Role = UserRole,
            Content = "message of B",
            CreateTime = DateTime.UtcNow
        });

        await using var context = CreateContext();
        var messagesOfA = await context.Set<LLMMessage>()
            .Where(m => m.ConversationId == conversationIdOfA)
            .ToListAsync();

        messagesOfA.Count.ShouldBe(2);
        messagesOfA.ShouldAllBe(m => m.Content != "message of B");
    }

    [Test]
    public async Task GetOrCreateConversationAsync_Owner_ReusesTheExistingRow()
    {
        var conversationIdOfA = await SeedConversationOfUserAAsync();

        var forUserA = await CreateRepository().GetOrCreateConversationAsync(SharedConversationId, UserA);

        forUserA.Id.ShouldBe(conversationIdOfA);
    }
}
