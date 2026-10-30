// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that the broadcast queries of the messaging repositories translate on the real Npgsql provider. They run against an
/// unreachable server: translation happens before a connection is opened, so an untranslatable expression fails with "could not be
/// translated", while a translatable one only fails on the connection.
/// </summary>

using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Persistence.Configurations;
using Klacks.Plugin.Messaging.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingRepositoriesBroadcastTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private const string TranslationFailureMarker = "could not be translated";

    private sealed class ModelOnlyContext(DbContextOptions<ModelOnlyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new MessagingProviderConfiguration());
            modelBuilder.ApplyConfiguration(new MessageConfiguration());
            modelBuilder.ApplyConfiguration(new MessengerContactConfiguration());
        }
    }

    private ModelOnlyContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ModelOnlyContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new ModelOnlyContext(options);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task TheRepeatCheck_CountsAClaimOfAnInterruptedRun_ButNotAFailedAttempt()
    {
        var providerId = Guid.NewGuid();
        var claimed = Guid.NewGuid();
        var sent = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ModelOnlyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new ModelOnlyContext(options);
        context.Set<Message>().AddRange(
            BroadcastRow(providerId, claimed, MessageStatus.Pending),
            BroadcastRow(providerId, sent, MessageStatus.Sent),
            BroadcastRow(providerId, failed, MessageStatus.Failed));
        await context.SaveChangesAsync();

        var delivered = await new MessageRepository(context)
            .GetRecentBroadcastClientIdsAsync(providerId, "Hallo", DateTime.UtcNow.AddMinutes(-1));

        delivered.ShouldBe([claimed, sent], ignoreOrder: true);
    }

    private static Message BroadcastRow(Guid providerId, Guid clientId, MessageStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ProviderId = providerId,
        ClientId = clientId,
        BroadcastId = Guid.NewGuid(),
        ExternalMessageId = string.Empty,
        Content = "Hallo",
        ContentType = "text",
        Direction = MessageDirection.Outbound,
        Status = status,
        Timestamp = DateTime.UtcNow
    };

    private static async Task ShouldTranslate(Func<Task> query)
    {
        var failure = await Should.ThrowAsync<Exception>(query);

        failure.Message.ShouldNotContain(TranslationFailureMarker);
        (failure is NpgsqlException || failure.InnerException is NpgsqlException).ShouldBeTrue(failure.ToString());
    }

    [Test]
    public Task TheRecentBroadcastQuery_Translates() =>
        ShouldTranslate(() => new MessageRepository(_context)
            .GetRecentBroadcastClientIdsAsync(Guid.NewGuid(), "Hallo", DateTime.UtcNow));

    [Test]
    public Task TheFirstContactsQuery_Translates() =>
        ShouldTranslate(() => new MessengerContactRepository(_context)
            .GetFirstContactsAsync(new[] { Guid.NewGuid() }, MessengerType.Telegram));
}
