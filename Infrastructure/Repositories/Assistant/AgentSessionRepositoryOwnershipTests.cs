// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Regression tests for the agent-session IDOR: reading the active messages of a session must be
/// scoped to the owning user, so a second user gets an empty list instead of another user's
/// transcript. Uses a shared in-memory DataBaseContext, mirroring the other assistant repository tests.
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class AgentSessionRepositoryOwnershipTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const string SessionKey = "session-1";
    private const string UserRole = "user";

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

    private AgentSessionRepository CreateRepository() => new(CreateContext());

    private async Task<Guid> SeedSessionOfUserAAsync()
    {
        await using var context = CreateContext();
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            SessionId = SessionKey,
            UserId = UserA,
            Status = AgentSessionStatus.Active,
            LastMessageAt = DateTime.UtcNow
        };
        context.AgentSessions.Add(session);
        context.AgentSessionMessages.Add(new AgentSessionMessage
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            Role = UserRole,
            Content = "secret of A",
            CreateTime = DateTime.UtcNow
        });

        await context.SaveChangesAsync();
        return session.Id;
    }

    [Test]
    public async Task GetActiveMessagesAsync_Owner_GetsTheTranscript()
    {
        var sessionId = await SeedSessionOfUserAAsync();

        var messages = await CreateRepository().GetActiveMessagesAsync(sessionId, UserA);

        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("secret of A");
    }

    [Test]
    public async Task GetActiveMessagesAsync_ForeignUser_GetsEmptyList()
    {
        var sessionId = await SeedSessionOfUserAAsync();

        var messages = await CreateRepository().GetActiveMessagesAsync(sessionId, UserB);

        messages.ShouldBeEmpty();
    }

    [Test]
    public async Task GetActiveMessagesAsync_ForeignUser_IsIndistinguishableFromAnUnknownSession()
    {
        await SeedSessionOfUserAAsync();

        var forUnknownSession = await CreateRepository().GetActiveMessagesAsync(Guid.NewGuid(), UserB);

        forUnknownSession.ShouldBeEmpty();
    }
}
