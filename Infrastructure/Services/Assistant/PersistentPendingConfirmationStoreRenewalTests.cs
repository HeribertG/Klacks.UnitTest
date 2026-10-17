// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PersistentPendingConfirmationStore.RenewOfferedAsync: the detour turn of an accepted explain offer must hand
/// the held confirmation back with a fresh lifetime, so the forced-redemption window (measured from the issue time) and the
/// token's own expiry cover the answer that follows the explanation. Only the ONE row the offer was made at is renewed -
/// matched by token fingerprint, skill, purpose, channel and user. In particular an older, unrelated confirmation of the same
/// user (a forgotten bulk deletion) is never pulled back into the force window, whatever offer is accepted; a row that
/// already expired is never revived.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Security;
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
public class PersistentPendingConfirmationStoreRenewalTests
{
    private const string DeleteSkill = "bulk_delete_works";
    private const string ScenarioSkill = "accept_scenario";

    private static readonly Guid User1 = Guid.NewGuid();
    private static readonly Guid User2 = Guid.NewGuid();
    private static readonly TimeSpan ForceWindow = TimeSpan.FromSeconds(AutonomyDefaults.ConfirmationForceWindowSeconds);
    private static readonly TimeSpan OlderThanWindow = ForceWindow + TimeSpan.FromMinutes(1);
    private static readonly string[] GateReplayOnly = [PendingConfirmationPurposes.GateReplay];
    private static readonly string[] ScenarioPurposes =
        [PendingConfirmationPurposes.GateReplay, PendingConfirmationPurposes.StaleScenarioOverwrite];

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private PersistentPendingConfirmationStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(Klacks.Api.Domain.Interfaces.Assistant.IPendingConfirmationRepository))
            .Returns(_ => new PendingConfirmationRepository(CreateContext()));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        _store = new PersistentPendingConfirmationStore(scopeFactory, NullLogger<PersistentPendingConfirmationStore>.Instance);
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static DateTime ExpiryForAge(TimeSpan age) =>
        DateTime.UtcNow.AddMinutes(AutonomyDefaults.ConfirmationTtlMinutes) - age;

    private void AddRow(
        string token, Guid userId, string skill, string purpose, string channel, DateTime expiresAtUtc)
    {
        using var context = CreateContext();
        context.PendingConfirmations.Add(new PendingConfirmationRow
        {
            Id = Guid.NewGuid(),
            Token = token,
            UserId = userId,
            SkillName = skill,
            ParametersJson = "{}",
            Purpose = purpose,
            Channel = channel,
            ExpiresAtUtc = expiresAtUtc
        });
        context.SaveChanges();
    }

    private DateTime ExpiryOf(string token)
    {
        using var verify = CreateContext();
        return verify.PendingConfirmations.Single(row => row.Token == token).ExpiresAtUtc;
    }

    private Task<bool> Renew(string token, string skill, IReadOnlyCollection<string> purposes, Guid? user = null) =>
        _store.RenewOfferedAsync(
            user ?? User1, ConfirmationTokenHasher.Hash(token), skill, purposes, PendingConfirmationChannels.Chat);

    [Test]
    public async Task TheOfferedToken_IsInsideTheForceWindowAgain()
    {
        AddRow("offered", User1, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat,
            ExpiryForAge(OlderThanWindow));
        _store.PeekLatestForUser(User1, ForceWindow).ShouldBeNull();

        (await Renew("offered", DeleteSkill, GateReplayOnly)).ShouldBeTrue();

        _store.PeekLatestForUser(User1, ForceWindow)!.Token.ShouldBe("offered");
    }

    [Test]
    public async Task AnOlderForeignRowOfTheSameUser_IsNeverRenewed()
    {
        var stale = ExpiryForAge(OlderThanWindow + TimeSpan.FromSeconds(30));
        AddRow("forgotten-delete", User1, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat, stale);
        AddRow("offered-scenario", User1, ScenarioSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat,
            ExpiryForAge(OlderThanWindow));

        (await Renew("offered-scenario", ScenarioSkill, ScenarioPurposes)).ShouldBeTrue();

        ExpiryOf("forgotten-delete").ShouldBe(stale);
        _store.PeekLatestForUser(User1, ForceWindow)!.Token.ShouldBe("offered-scenario");
    }

    [Test]
    public async Task AFingerprintOfAnotherSkillsToken_RenewsNothing()
    {
        var stale = ExpiryForAge(OlderThanWindow);
        AddRow("forgotten-delete", User1, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat, stale);

        (await Renew("forgotten-delete", ScenarioSkill, ScenarioPurposes)).ShouldBeFalse();

        ExpiryOf("forgotten-delete").ShouldBe(stale);
        _store.PeekLatestForUser(User1, ForceWindow).ShouldBeNull();
    }

    [Test]
    public async Task APurposeTheTopicMayNotRenew_IsLeftAlone()
    {
        var stale = ExpiryForAge(OlderThanWindow);
        AddRow("stale-overwrite", User1, DeleteSkill, PendingConfirmationPurposes.StaleScenarioOverwrite,
            PendingConfirmationChannels.Chat, stale);
        AddRow("undo", User1, DeleteSkill, PendingConfirmationPurposes.CorrectionUndo, PendingConfirmationChannels.Chat, stale);

        (await Renew("stale-overwrite", DeleteSkill, GateReplayOnly)).ShouldBeFalse();
        (await Renew("undo", DeleteSkill, GateReplayOnly)).ShouldBeFalse();

        ExpiryOf("stale-overwrite").ShouldBe(stale);
        ExpiryOf("undo").ShouldBe(stale);
    }

    [Test]
    public async Task AStaleScenarioOverwrite_IsRenewedForTheScenarioTopic()
    {
        AddRow("stale-scenario", User1, ScenarioSkill, PendingConfirmationPurposes.StaleScenarioOverwrite,
            PendingConfirmationChannels.Chat, ExpiryForAge(OlderThanWindow));

        (await Renew("stale-scenario", ScenarioSkill, ScenarioPurposes)).ShouldBeTrue();

        _store.PeekLatestForUser(
                User1, ForceWindow, PendingConfirmationPurposes.StaleScenarioOverwrite, PendingConfirmationChannels.Chat)!
            .Token.ShouldBe("stale-scenario");
    }

    [Test]
    public async Task AnotherChannelOrAnotherUser_IsLeftAlone()
    {
        var stale = ExpiryForAge(OlderThanWindow);
        AddRow("mcp", User1, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Mcp, stale);
        AddRow("other-user", User2, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat, stale);

        (await Renew("mcp", DeleteSkill, GateReplayOnly)).ShouldBeFalse();
        (await Renew("other-user", DeleteSkill, GateReplayOnly, User1)).ShouldBeFalse();

        ExpiryOf("mcp").ShouldBe(stale);
        ExpiryOf("other-user").ShouldBe(stale);
    }

    [Test]
    public async Task AnExpiredToken_IsNotRevived()
    {
        AddRow("expired", User1, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat,
            DateTime.UtcNow.AddMinutes(-1));

        (await Renew("expired", DeleteSkill, GateReplayOnly)).ShouldBeFalse();

        _store.PeekLatestForUser(User1, TimeSpan.FromMinutes(AutonomyDefaults.ConfirmationTtlMinutes)).ShouldBeNull();
    }

    [Test]
    public async Task WithoutAnyRow_OrWithoutPurposes_NothingHappens()
    {
        (await Renew("missing", DeleteSkill, GateReplayOnly)).ShouldBeFalse();

        AddRow("offered", User1, DeleteSkill, PendingConfirmationPurposes.GateReplay, PendingConfirmationChannels.Chat,
            ExpiryForAge(OlderThanWindow));
        (await Renew("offered", DeleteSkill, [])).ShouldBeFalse();
        _store.PeekLatestForUser(User1, ForceWindow).ShouldBeNull();
    }
}
