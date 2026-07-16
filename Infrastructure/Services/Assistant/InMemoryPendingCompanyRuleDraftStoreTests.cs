// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InMemoryPendingCompanyRuleDraftStore: Set/Get/Clear round-trip, expiry once
/// ExpiresAtUtc has passed, sliding expiration (each Set pushes ExpiresAtUtc a full TTL into the future,
/// even for a draft that was about to expire), and isolation between distinct (user, conversation) pairs.
/// </summary>

using System;
using Shouldly;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class InMemoryPendingCompanyRuleDraftStoreTests
{
    private InMemoryPendingCompanyRuleDraftStore _sut = null!;
    private const string ConversationId = "conversation-1";

    [SetUp]
    public void Setup()
    {
        _sut = new InMemoryPendingCompanyRuleDraftStore();
    }

    private static CompanyRuleDraft Draft()
    {
        return new CompanyRuleDraft
        {
            Kind = CompanyRuleKind.CounterRule,
            RuleText = "no more than 25 night shifts per year"
        };
    }

    [Test]
    public void Set_Then_Get_Returns_The_Draft()
    {
        var userId = Guid.NewGuid();
        var draft = Draft();

        _sut.Set(userId, ConversationId, draft);

        _sut.Get(userId, ConversationId).ShouldBeSameAs(draft);
    }

    [Test]
    public void Set_Assigns_ExpiresAtUtc_A_Full_Ttl_Into_The_Future()
    {
        var userId = Guid.NewGuid();
        var draft = Draft();

        _sut.Set(userId, ConversationId, draft);

        draft.ExpiresAtUtc.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(CompanyRuleDraftDefaults.PendingDraftTtlMinutes - 1));
        draft.ExpiresAtUtc.ShouldBeLessThanOrEqualTo(DateTime.UtcNow.AddMinutes(CompanyRuleDraftDefaults.PendingDraftTtlMinutes));
    }

    [Test]
    public void Get_Returns_Null_When_Nothing_Stored()
    {
        _sut.Get(Guid.NewGuid(), ConversationId).ShouldBeNull();
    }

    [Test]
    public void Clear_Removes_The_Draft()
    {
        var userId = Guid.NewGuid();
        _sut.Set(userId, ConversationId, Draft());

        _sut.Clear(userId, ConversationId);

        _sut.Get(userId, ConversationId).ShouldBeNull();
    }

    [Test]
    public void Get_Returns_Null_Once_ExpiresAtUtc_Has_Passed()
    {
        var userId = Guid.NewGuid();
        var draft = Draft();
        _sut.Set(userId, ConversationId, draft);

        draft.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);

        _sut.Get(userId, ConversationId).ShouldBeNull();
    }

    [Test]
    public void Get_Returns_Draft_While_Within_Ttl()
    {
        var userId = Guid.NewGuid();
        var draft = Draft();
        _sut.Set(userId, ConversationId, draft);

        draft.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);

        _sut.Get(userId, ConversationId).ShouldBeSameAs(draft);
    }

    [Test]
    public void Set_Slides_Expiration_Forward_Even_When_The_Draft_Was_About_To_Expire()
    {
        var userId = Guid.NewGuid();
        var draft = Draft();
        _sut.Set(userId, ConversationId, draft);
        draft.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(1);

        _sut.Set(userId, ConversationId, draft);

        draft.ExpiresAtUtc.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(CompanyRuleDraftDefaults.PendingDraftTtlMinutes - 1));
        _sut.Get(userId, ConversationId).ShouldBeSameAs(draft);
    }

    [Test]
    public void Drafts_Are_Isolated_Between_Users_And_Conversations()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var draft = Draft();

        _sut.Set(userA, ConversationId, draft);

        _sut.Get(userB, ConversationId).ShouldBeNull();
        _sut.Get(userA, "other-conversation").ShouldBeNull();
        _sut.Get(userA, ConversationId).ShouldBeSameAs(draft);
    }

    [Test]
    public void Set_Replaces_The_Previous_Draft_For_The_Same_Key()
    {
        var userId = Guid.NewGuid();
        var first = Draft();
        var second = Draft();

        _sut.Set(userId, ConversationId, first);
        _sut.Set(userId, ConversationId, second);

        _sut.Get(userId, ConversationId).ShouldBeSameAs(second);
    }
}
