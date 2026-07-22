// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PersistentPendingConfirmationStore: verifies the DB-backed store round-trips a pending
/// confirmation (skill name and parameters, including non-string values), consumes a token exactly once
/// with a hard delete as proof, burns a token on first use even when the user or skill mismatches (the
/// same security property the in-memory store had), hard-deletes an expired token on consume, prunes
/// globally expired rows on Create, and reproduces PeekLatestForUser's non-destructive/most-recent/
/// user-scoped/recency-window semantics. Uses a shared in-memory DataBaseContext resolved through an
/// IServiceScopeFactory, mirroring the store's own fresh-scope-per-operation design.
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
public class PersistentPendingConfirmationStoreTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private PersistentPendingConfirmationStore _store = null!;

    private static readonly Guid User1 = Guid.NewGuid();
    private static readonly Guid User2 = Guid.NewGuid();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IPendingConfirmationRepository))
            .Returns(_ => new PendingConfirmationRepository(CreateContext()));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _store = new PersistentPendingConfirmationStore(scopeFactory);
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static Dictionary<string, object> SampleParameters() => new()
    {
        ["name"] = "Hans",
        ["count"] = 5,
        ["approved"] = true
    };

    [Test]
    public void Create_then_Consume_round_trips_skill_and_parameters()
    {
        var token = _store.Create(User1, "delete_system_user", SampleParameters());

        var consumed = _store.Consume(token, User1);

        consumed.ShouldNotBeNull();
        consumed!.SkillName.ShouldBe("delete_system_user");
        consumed.UserId.ShouldBe(User1);
        consumed.Parameters["name"].ToString().ShouldBe("Hans");
        consumed.Parameters["count"].ToString().ShouldBe("5");
        consumed.Parameters["approved"].ToString().ShouldBe(true.ToString());
    }

    [Test]
    public void Consume_returns_null_on_second_attempt()
    {
        var token = _store.Create(User1, "delete_system_user", SampleParameters());

        _store.Consume(token, User1).ShouldNotBeNull();
        _store.Consume(token, User1).ShouldBeNull();
    }

    [Test]
    public void Consume_hard_deletes_the_row()
    {
        var token = _store.Create(User1, "delete_system_user", SampleParameters());

        _store.Consume(token, User1);

        using var verify = CreateContext();
        verify.PendingConfirmations.Count().ShouldBe(0);
    }

    [Test]
    public void Consume_returns_null_for_unknown_token()
    {
        _store.Consume("does-not-exist", User1).ShouldBeNull();
    }

    [Test]
    public void Consume_with_wrong_user_returns_null_and_still_burns_the_token()
    {
        var token = _store.Create(User1, "delete_system_user", SampleParameters());

        _store.Consume(token, User2).ShouldBeNull();

        using var verify = CreateContext();
        verify.PendingConfirmations.Count().ShouldBe(0);
        _store.Consume(token, User1).ShouldBeNull();
    }

    [Test]
    public void Consume_with_wrong_skill_name_returns_null_and_still_burns_the_token()
    {
        var token = _store.Create(User1, "delete_system_user", SampleParameters());

        _store.Consume(token, User1, "assign_user_permissions").ShouldBeNull();

        using var verify = CreateContext();
        verify.PendingConfirmations.Count().ShouldBe(0);
        _store.Consume(token, User1).ShouldBeNull();
    }

    [Test]
    public void Consume_returns_null_and_hard_deletes_an_expired_token()
    {
        const string token = "expired-token";
        using (var context = CreateContext())
        {
            context.PendingConfirmations.Add(new PendingConfirmationRow
            {
                Id = Guid.NewGuid(),
                Token = token,
                UserId = User1,
                SkillName = "delete_system_user",
                ParametersJson = "{}",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            context.SaveChanges();
        }

        _store.Consume(token, User1).ShouldBeNull();

        using var verify = CreateContext();
        verify.PendingConfirmations.Count().ShouldBe(0);
    }

    [Test]
    public void Create_prunes_globally_expired_rows()
    {
        using (var context = CreateContext())
        {
            context.PendingConfirmations.Add(new PendingConfirmationRow
            {
                Id = Guid.NewGuid(),
                Token = "stale-token",
                UserId = User2,
                SkillName = "stale_skill",
                ParametersJson = "{}",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            context.SaveChanges();
        }

        _store.Create(User1, "delete_system_user", SampleParameters());

        using var verify = CreateContext();
        verify.PendingConfirmations.Count(p => p.UserId == User2).ShouldBe(0);
        verify.PendingConfirmations.Count(p => p.UserId == User1).ShouldBe(1);
    }

    [Test]
    public void PeekLatestForUser_returns_null_when_no_pending()
    {
        _store.PeekLatestForUser(User1, Window).ShouldBeNull();
    }

    [Test]
    public void PeekLatestForUser_returns_token_and_skill_for_user()
    {
        var token = _store.Create(User1, "assign_user_permissions", SampleParameters());

        var handle = _store.PeekLatestForUser(User1, Window);

        handle.ShouldNotBeNull();
        handle!.Token.ShouldBe(token);
        handle.SkillName.ShouldBe("assign_user_permissions");
    }

    [Test]
    public void PeekLatestForUser_does_not_consume()
    {
        var token = _store.Create(User1, "delete_system_user", SampleParameters());

        _store.PeekLatestForUser(User1, Window).ShouldNotBeNull();

        _store.Consume(token, User1).ShouldNotBeNull();
    }

    [Test]
    public void PeekLatestForUser_is_scoped_to_user()
    {
        _store.Create(User1, "delete_system_user", SampleParameters());

        _store.PeekLatestForUser(User2, Window).ShouldBeNull();
    }

    [Test]
    public void PeekLatestForUser_returns_most_recent_when_multiple()
    {
        _store.Create(User1, "first_skill", SampleParameters());
        Thread.Sleep(10);
        var secondToken = _store.Create(User1, "second_skill", SampleParameters());

        var handle = _store.PeekLatestForUser(User1, Window);

        handle.ShouldNotBeNull();
        handle!.Token.ShouldBe(secondToken);
        handle.SkillName.ShouldBe("second_skill");
    }

    [Test]
    public void PeekLatestForUser_returns_null_after_consumption()
    {
        var token = _store.Create(User1, "set_autonomy_level", SampleParameters());
        _store.Consume(token, User1);

        _store.PeekLatestForUser(User1, Window).ShouldBeNull();
    }

    [Test]
    public void PeekLatestForUser_excludes_pending_older_than_window()
    {
        _store.Create(User1, "delete_system_user", SampleParameters());

        _store.PeekLatestForUser(User1, TimeSpan.FromMilliseconds(-1)).ShouldBeNull();
    }
}
