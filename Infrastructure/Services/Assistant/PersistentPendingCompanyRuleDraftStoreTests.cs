// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PersistentPendingCompanyRuleDraftStore: verifies the DB-backed store round-trips a draft
/// (kind, rule text, name and parameters), replaces a prior draft for the same key, hard-deletes on Clear
/// and on TTL expiry, and isolates entries by user and conversation. Uses a shared in-memory
/// DataBaseContext resolved through an IServiceScopeFactory, mirroring the store's own
/// fresh-scope-per-operation design. Assertions compare values rather than reference identity because
/// Get() always returns a freshly deserialized object.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
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
public class PersistentPendingCompanyRuleDraftStoreTests
{
    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private PersistentPendingCompanyRuleDraftStore _store = null!;

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
        provider.GetService(typeof(IPendingCompanyRuleDraftRepository))
            .Returns(_ => new PendingCompanyRuleDraftRepository(CreateContext()));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _store = new PersistentPendingCompanyRuleDraftStore(scopeFactory);
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static CompanyRuleDraft Draft() => new()
    {
        Kind = CompanyRuleKind.CounterRule,
        RuleText = "no more than 25 night shifts per year",
        Name = "night-shift-cap",
        Parameters = new Dictionary<string, string>
        {
            ["maxCount"] = "25"
        }
    };

    [Test]
    public void Set_then_Get_returns_an_equivalent_draft()
    {
        _store.Set(User1, Conversation1, Draft());

        var fetched = _store.Get(User1, Conversation1);

        fetched.ShouldNotBeNull();
        fetched!.Kind.ShouldBe(CompanyRuleKind.CounterRule);
        fetched.RuleText.ShouldBe("no more than 25 night shifts per year");
        fetched.Name.ShouldBe("night-shift-cap");
        fetched.Parameters["maxCount"].ShouldBe("25");
    }

    [Test]
    public void Set_assigns_ExpiresAtUtc_a_full_ttl_into_the_future()
    {
        var draft = Draft();

        _store.Set(User1, Conversation1, draft);

        draft.ExpiresAtUtc.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(CompanyRuleDraftDefaults.PendingDraftTtlMinutes - 1));
        draft.ExpiresAtUtc.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(CompanyRuleDraftDefaults.PendingDraftTtlMinutes));
    }

    [Test]
    public void Get_returns_null_when_nothing_stored()
    {
        _store.Get(Guid.NewGuid(), Conversation1).ShouldBeNull();
    }

    [Test]
    public void Clear_hard_deletes_the_row()
    {
        _store.Set(User1, Conversation1, Draft());

        _store.Clear(User1, Conversation1);

        _store.Get(User1, Conversation1).ShouldBeNull();
        using var verify = CreateContext();
        verify.PendingCompanyRuleDrafts.Count().ShouldBe(0);
    }

    [Test]
    public void Get_returns_null_and_hard_deletes_after_ttl_expiry()
    {
        using (var context = CreateContext())
        {
            context.PendingCompanyRuleDrafts.Add(new PendingCompanyRuleDraftRow
            {
                Id = Guid.NewGuid(),
                UserId = User1,
                ConversationId = Conversation1,
                DraftJson = "{}",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            context.SaveChanges();
        }

        _store.Get(User1, Conversation1).ShouldBeNull();

        using var verify = CreateContext();
        verify.PendingCompanyRuleDrafts.Count().ShouldBe(0);
    }

    [Test]
    public void Get_returns_draft_while_within_ttl()
    {
        _store.Set(User1, Conversation1, Draft());

        _store.Get(User1, Conversation1).ShouldNotBeNull();
    }

    [Test]
    public void Set_replaces_the_previous_draft_for_the_same_key()
    {
        _store.Set(User1, Conversation1, Draft());

        var updated = Draft();
        updated.Name = "replacement-rule";
        updated.Parameters["maxCount"] = "30";
        _store.Set(User1, Conversation1, updated);

        var fetched = _store.Get(User1, Conversation1);
        fetched!.Name.ShouldBe("replacement-rule");
        fetched.Parameters["maxCount"].ShouldBe("30");

        using var verify = CreateContext();
        verify.PendingCompanyRuleDrafts.Count(p => p.UserId == User1 && p.ConversationId == Conversation1)
            .ShouldBe(1);
    }

    [Test]
    public void Drafts_are_isolated_between_users_and_conversations()
    {
        _store.Set(User1, Conversation1, Draft());

        _store.Get(User2, Conversation1).ShouldBeNull();
        _store.Get(User1, Conversation2).ShouldBeNull();
        _store.Get(User1, Conversation1).ShouldNotBeNull();
    }
}
