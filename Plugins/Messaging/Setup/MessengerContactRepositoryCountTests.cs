// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for MessengerContactRepository.CountByTypeAsync: counts distinct clients per messenger type and
/// ignores deleted contacts and other types.
/// </summary>
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Persistence.Configurations;
using Klacks.Plugin.Messaging.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Setup;

[TestFixture]
public class MessengerContactRepositoryCountTests
{
    [Test]
    public async Task CountByTypeAsync_CountsDistinctClientsOfTypeWithoutDeleted()
    {
        await using var context = new ContactContext(new DbContextOptionsBuilder<ContactContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var twoContactsClient = Guid.NewGuid();
        context.Set<MessengerContact>().AddRange(
            Contact(twoContactsClient, MessengerType.Telegram, "1"),
            Contact(twoContactsClient, MessengerType.Telegram, "2"),
            Contact(Guid.NewGuid(), MessengerType.Telegram, "3"),
            Contact(Guid.NewGuid(), MessengerType.Telegram, "4", isDeleted: true),
            Contact(Guid.NewGuid(), MessengerType.Slack, "5"));
        await context.SaveChangesAsync();
        var sut = new MessengerContactRepository(context);

        var count = await sut.CountByTypeAsync(MessengerType.Telegram);

        count.ShouldBe(2);
    }

    private static MessengerContact Contact(Guid clientId, MessengerType type, string value, bool isDeleted = false) =>
        new() { Id = Guid.NewGuid(), ClientId = clientId, Type = type, Value = value, IsDeleted = isDeleted };

    private sealed class ContactContext : DbContext
    {
        public ContactContext(DbContextOptions<ContactContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfiguration(new MessengerContactConfiguration());
        }
    }
}
