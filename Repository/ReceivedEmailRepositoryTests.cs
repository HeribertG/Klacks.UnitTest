// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Repositories.Email;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ReceivedEmailRepositoryTests : BaseRepositoryTest
{
    private const string InboxFolder = "INBOX";

    private ReceivedEmailRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _repository = new ReceivedEmailRepository(
            TestDbContext, Substitute.For<ILogger<ReceivedEmailRepository>>());
    }

    [Test]
    public async Task GetFilteredListAsync_SoftDeletedEmail_IsNotReturned()
    {
        var activeEmail = await SeedActiveAndSoftDeletedEmailAsync();

        var result = await _repository.GetFilteredListAsync(null, null, false, 0, 50);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(activeEmail.Id);
    }

    [Test]
    public async Task GetFilteredListAsync_WithFolderFilter_SoftDeletedEmail_IsNotReturned()
    {
        var activeEmail = await SeedActiveAndSoftDeletedEmailAsync();

        var result = await _repository.GetFilteredListAsync(InboxFolder, null, false, 0, 50);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(activeEmail.Id);
    }

    [Test]
    public async Task GetFilteredCountAsync_SoftDeletedEmail_IsNotCounted()
    {
        await SeedActiveAndSoftDeletedEmailAsync();

        var count = await _repository.GetFilteredCountAsync(null, null);

        count.ShouldBe(1);
    }

    [Test]
    public async Task GetFilteredCountAsync_WithFolderFilter_SoftDeletedEmail_IsNotCounted()
    {
        await SeedActiveAndSoftDeletedEmailAsync();

        var count = await _repository.GetFilteredCountAsync(InboxFolder, null);

        count.ShouldBe(1);
    }

    [Test]
    public async Task GetUnprocessedAsync_ReturnsOnlyEmailsWithoutProcessedAt()
    {
        var unprocessed = CreateEmail("unprocessed-message-id");
        var processed = CreateEmail("processed-message-id");
        processed.ProcessedAt = DateTime.UtcNow;

        await TestDbContext.ReceivedEmails.AddRangeAsync(unprocessed, processed);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetUnprocessedAsync(50);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(unprocessed.Id);
    }

    [Test]
    public async Task GetUnprocessedAsync_ReturnsTrackedEntities_SoMutationsCanBeSaved()
    {
        var unprocessed = CreateEmail("trackable-message-id");
        await TestDbContext.ReceivedEmails.AddAsync(unprocessed);
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetUnprocessedAsync(50);
        result[0].ProcessedAt = DateTime.UtcNow;
        await TestDbContext.SaveChangesAsync();

        var reloaded = await _repository.GetUnprocessedAsync(50);
        reloaded.ShouldBeEmpty();
    }

    [Test]
    public async Task GetUnprocessedAsync_RespectsTakeLimit()
    {
        for (var i = 0; i < 3; i++)
        {
            await TestDbContext.ReceivedEmails.AddAsync(CreateEmail($"limit-message-id-{i}"));
        }
        await TestDbContext.SaveChangesAsync();

        var result = await _repository.GetUnprocessedAsync(2);

        result.Count.ShouldBe(2);
    }

    private async Task<ReceivedEmail> SeedActiveAndSoftDeletedEmailAsync()
    {
        var activeEmail = CreateEmail("active-message-id");
        var deletedEmail = CreateEmail("deleted-message-id");

        await TestDbContext.ReceivedEmails.AddRangeAsync(activeEmail, deletedEmail);
        await TestDbContext.SaveChangesAsync();

        TestDbContext.ReceivedEmails.Remove(deletedEmail);
        await TestDbContext.SaveChangesAsync();

        deletedEmail.IsDeleted.ShouldBeTrue();

        return activeEmail;
    }

    private static ReceivedEmail CreateEmail(string messageId)
    {
        return new ReceivedEmail
        {
            Id = Guid.NewGuid(),
            MessageId = messageId,
            Folder = InboxFolder,
            FromAddress = "sender@example.com",
            ToAddress = "recipient@example.com",
            Subject = "Test",
            ReceivedDate = DateTime.UtcNow,
        };
    }
}
