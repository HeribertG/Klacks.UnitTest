// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PersistentPendingRecipeStore: verifies the DB-backed store round-trips a paused recipe
/// (slot bag and flags), replaces a prior pause for the same key, hard-deletes on Clear and on TTL
/// expiry, and isolates entries by user and conversation. Uses a shared in-memory DataBaseContext
/// resolved through an IServiceScopeFactory, mirroring the store's own fresh-scope-per-operation design.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class PersistentPendingRecipeStoreTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private PersistentPendingRecipeStore _store = null!;

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

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPendingRecipeRepository))
            .Returns(_ => new PendingRecipeRepository(CreateContext()));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _store = new PersistentPendingRecipeStore(scopeFactory);
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static PendingRecipe SamplePending(Guid userId, string conversationId) => new()
    {
        UserId = userId,
        ConversationId = conversationId,
        RecipeName = "onboard-employee",
        StepIndex = 2,
        Slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "Hans",
            ["group"] = "Bern"
        },
        AwaitingConfirmation = true,
        CaptureRewindUsed = true
    };

    [Test]
    public void Save_then_Peek_round_trips_all_fields()
    {
        _store.Save(SamplePending(User1, Conversation1));

        var peeked = _store.Peek(User1, Conversation1);

        peeked.ShouldNotBeNull();
        peeked!.RecipeName.ShouldBe("onboard-employee");
        peeked.StepIndex.ShouldBe(2);
        peeked.AwaitingConfirmation.ShouldBeTrue();
        peeked.CaptureRewindUsed.ShouldBeTrue();
        peeked.Slots["name"].ShouldBe("Hans");
        peeked.Slots["GROUP"].ShouldBe("Bern");
    }

    [Test]
    public void Save_replaces_prior_pause_for_same_key()
    {
        _store.Save(SamplePending(User1, Conversation1));

        var updated = SamplePending(User1, Conversation1);
        updated.RecipeName = "create-group";
        updated.StepIndex = 5;
        _store.Save(updated);

        var peeked = _store.Peek(User1, Conversation1);
        peeked!.RecipeName.ShouldBe("create-group");
        peeked.StepIndex.ShouldBe(5);

        using var context = CreateContext();
        context.PendingRecipes.Count(p => p.UserId == User1 && p.ConversationId == Conversation1)
            .ShouldBe(1);
    }

    [Test]
    public void Peek_returns_null_and_hard_deletes_after_ttl_expiry()
    {
        using (var context = CreateContext())
        {
            context.PendingRecipes.Add(new PendingRecipeRow
            {
                Id = Guid.NewGuid(),
                UserId = User1,
                ConversationId = Conversation1,
                RecipeName = "onboard-employee",
                StepIndex = 0,
                SlotsJson = "{}",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1),
                AwaitingConfirmation = false,
                CaptureRewindUsed = false
            });
            context.SaveChanges();
        }

        var peeked = _store.Peek(User1, Conversation1);

        peeked.ShouldBeNull();
        using var verify = CreateContext();
        verify.PendingRecipes.Count().ShouldBe(0);
    }

    [Test]
    public void Clear_hard_deletes_the_row()
    {
        _store.Save(SamplePending(User1, Conversation1));

        _store.Clear(User1, Conversation1);

        _store.Peek(User1, Conversation1).ShouldBeNull();
        using var verify = CreateContext();
        verify.PendingRecipes.Count().ShouldBe(0);
    }

    [Test]
    public void Peek_isolates_by_user_and_conversation()
    {
        _store.Save(SamplePending(User1, Conversation1));

        _store.Peek(User1, Conversation2).ShouldBeNull();
        _store.Peek(User2, Conversation1).ShouldBeNull();
        _store.Peek(User1, Conversation1).ShouldNotBeNull();
    }

    [Test]
    public void Peek_returns_null_when_nothing_pending()
    {
        _store.Peek(User1, Conversation1).ShouldBeNull();
    }

    [Test]
    public void Save_prunes_globally_expired_rows()
    {
        using (var context = CreateContext())
        {
            context.PendingRecipes.Add(new PendingRecipeRow
            {
                Id = Guid.NewGuid(),
                UserId = User2,
                ConversationId = Conversation2,
                RecipeName = "stale",
                StepIndex = 0,
                SlotsJson = "{}",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
                AwaitingConfirmation = false,
                CaptureRewindUsed = false
            });
            context.SaveChanges();
        }

        _store.Save(SamplePending(User1, Conversation1));

        using var verify = CreateContext();
        verify.PendingRecipes.Count(p => p.UserId == User2).ShouldBe(0);
        verify.PendingRecipes.Count(p => p.UserId == User1).ShouldBe(1);
    }
}
