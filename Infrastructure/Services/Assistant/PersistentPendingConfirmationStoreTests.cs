// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PersistentPendingConfirmationStore: verifies the DB-backed store round-trips a pending
/// confirmation (skill name and parameters, including non-string values), consumes a token exactly once
/// with a hard delete as proof, burns a token on first use even when the user or skill mismatches (the
/// same security property the in-memory store had), hard-deletes an expired token on consume, prunes
/// globally expired rows on Create, and reproduces PeekLatestForUser's non-destructive/most-recent/
/// user-scoped/recency-window semantics. Uses a shared in-memory DataBaseContext resolved through an
/// IServiceScopeFactory, mirroring the store's own fresh-scope-per-operation design.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

        _store = new PersistentPendingConfirmationStore(scopeFactory, NullLogger<PersistentPendingConfirmationStore>.Instance);
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
    public void A_token_issued_over_mcp_is_invisible_to_the_chat_and_not_redeemable_by_it()
    {
        var mcpToken = _store.Create(
            User1, "delete_shift", SampleParameters(), PendingConfirmationPurposes.GateReplay,
            PendingConfirmationChannels.Mcp);

        _store.PeekLatestForUser(User1, Window).ShouldBeNull();
        _store.PeekLatestForUser(User1, Window, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Mcp)
            .ShouldNotBeNull();
        _store.Consume(mcpToken, User1).ShouldBeNull();
        _store.Consume(mcpToken, User1, channel: PendingConfirmationChannels.Mcp).ShouldBeNull();
    }

    [Test]
    public void A_token_issued_over_mcp_is_redeemable_on_mcp_itself()
    {
        var mcpToken = _store.Create(
            User1, "delete_shift", SampleParameters(), PendingConfirmationPurposes.GateReplay,
            PendingConfirmationChannels.Mcp);

        _store.Consume(mcpToken, User1, channel: PendingConfirmationChannels.Mcp).ShouldNotBeNull();
    }

    [Test]
    public void The_latest_chat_token_is_found_although_a_newer_mcp_token_exists()
    {
        var chatToken = _store.Create(User1, "delete_shift", SampleParameters());
        _store.Create(
            User1, "delete_shift", SampleParameters(), PendingConfirmationPurposes.GateReplay,
            PendingConfirmationChannels.Mcp);

        _store.PeekLatestForUser(User1, Window)!.Token.ShouldBe(chatToken);
    }

    [Test]
    public void A_row_stored_without_a_channel_reads_as_chat()
    {
        using (var context = CreateContext())
        {
            context.PendingConfirmations.Add(new PendingConfirmationRow
            {
                Id = Guid.NewGuid(),
                Token = "legacy-token",
                UserId = User1,
                SkillName = "delete_shift",
                ParametersJson = "{}",
                Channel = string.Empty,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(AutonomyDefaults.ConfirmationTtlMinutes)
            });
            context.SaveChanges();
        }

        _store.PeekLatestForUser(User1, Window)!.Token.ShouldBe("legacy-token");
        _store.Consume("legacy-token", User1).ShouldNotBeNull();
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

    [Test]
    public void DiscardByTokens_DropsExactlyTheGivenTokensOfThatUser()
    {
        var dropped = _store.Create(User1, "delete_group", SampleParameters());
        var alsoDropped = _store.Create(User1, "create_plan", SampleParameters());
        var kept = _store.Create(User1, "delete_client", SampleParameters());
        var othersToken = _store.Create(User2, "delete_group", SampleParameters());

        _store.DiscardByTokens(User1, [dropped, alsoDropped, othersToken, "unknown-token"]);

        _store.Consume(dropped, User1).ShouldBeNull();
        _store.Consume(alsoDropped, User1).ShouldBeNull();
        _store.Consume(kept, User1).ShouldNotBeNull();
        _store.Consume(othersToken, User2).ShouldNotBeNull();
    }

    [Test]
    public void DiscardByTokens_DropsATokenWhateverItsPurpose()
    {
        _store.Create(User1, "delete_group", SampleParameters(), PendingConfirmationPurposes.CorrectionUndo);
        var undo = _store.PeekLatestForUser(User1, Window, PendingConfirmationPurposes.CorrectionUndo)!;

        _store.DiscardByTokens(User1, [undo.Token]);

        _store.PeekLatestForUser(User1, Window, PendingConfirmationPurposes.CorrectionUndo).ShouldBeNull();
    }

    [Test]
    public void A_binding_given_to_Create_comes_back_identical_from_Consume()
    {
        var binding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, new[] { "w2", "w1" });
        var token = _store.Create(
            User1, "bulk_delete_works", SampleParameters(), PendingConfirmationPurposes.GateReplay,
            PendingConfirmationChannels.Chat, binding);

        var consumed = _store.Consume(token, User1);

        consumed.ShouldNotBeNull();
        consumed.Binding.ShouldBe(binding);
    }

    [Test]
    public void A_fingerprint_only_binding_comes_back_from_Consume_without_items_and_still_matches()
    {
        var big = ConfirmationBinding.Of(
            ConfirmationBindingKinds.Work,
            Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems + 1).Select(i => $"w{i:D3}"));
        big.Items.ShouldBeEmpty();
        var token = _store.Create(
            User1, "bulk_delete_works", SampleParameters(), PendingConfirmationPurposes.GateReplay,
            PendingConfirmationChannels.Chat, big);

        var consumed = _store.Consume(token, User1);

        consumed.ShouldNotBeNull();
        consumed.Binding.ShouldNotBeNull();
        consumed.Binding.Items.ShouldBeEmpty();
        consumed.Binding.Fingerprint.ShouldBe(big.Fingerprint);
        consumed.Binding.Matches(big).ShouldBeTrue();
    }

    [Test]
    public void Without_a_binding_Consume_returns_none()
    {
        var token = _store.Create(User1, "delete_shift", SampleParameters());

        _store.Consume(token, User1)!.Binding.ShouldBeNull();
    }

    [Test]
    public void The_binding_is_stored_in_its_own_column_and_not_in_the_parameters()
    {
        var binding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, new[] { "w1" });
        _store.Create(
            User1, "bulk_delete_works", SampleParameters(), PendingConfirmationPurposes.GateReplay,
            PendingConfirmationChannels.Chat, binding);

        using var verify = CreateContext();
        var row = verify.PendingConfirmations.Single();
        row.BindingJson.ShouldNotBeNullOrEmpty();
        row.ParametersJson.ShouldNotContain(binding.Fingerprint);
    }

    [TestCase("{garbage")]
    [TestCase("{}")]
    [TestCase("null")]
    public void An_unreadable_stored_binding_voids_the_token(string bindingJson)
    {
        using (var context = CreateContext())
        {
            context.PendingConfirmations.Add(new PendingConfirmationRow
            {
                Id = Guid.NewGuid(),
                Token = "corrupt-binding",
                UserId = User1,
                SkillName = "bulk_delete_works",
                ParametersJson = "{}",
                BindingJson = bindingJson,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(AutonomyDefaults.ConfirmationTtlMinutes)
            });
            context.SaveChanges();
        }

        _store.Consume("corrupt-binding", User1).ShouldBeNull();

        using var verify = CreateContext();
        verify.PendingConfirmations.Count().ShouldBe(0);
    }

    [Test]
    public void DiscardByTokens_WithNoTokens_TouchesNothing()
    {
        var token = _store.Create(User1, "delete_group", SampleParameters());

        _store.DiscardByTokens(User1, []);

        _store.Consume(token, User1).ShouldNotBeNull();
    }
}
