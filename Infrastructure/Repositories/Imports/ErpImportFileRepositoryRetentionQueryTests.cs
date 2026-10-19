// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportFileRepository.ListExpiredAsync on the EF in-memory provider: which ledger rows the
/// retention purge sees. Due are live Completed/CompletedWithWarnings rows finished strictly before the cutoff (a
/// row finished exactly at the cutoff is not due; without finish time the receipt counts) and soft-deleted rows
/// deleted strictly before the cutoff (never without deletion time). Received, Importing, Failed and Interrupted
/// rows are never listed, however old. Other drop points are ignored, the oldest rows come first and the limit
/// caps the result. The conditional hard deletes use ExecuteDeleteAsync, which the in-memory provider cannot run;
/// they are proven against PostgreSQL in Klacks.IntegrationTest (ErpImportRetentionIntegrationTests).
/// </summary>

using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Imports;

[TestFixture]
public class ErpImportFileRepositoryRetentionQueryTests
{
    private const int NoLimit = 100;

    private static readonly DateTime Cutoff = new(2026, 7, 21, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private Guid _dropPointId;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
        _dropPointId = Guid.NewGuid();
    }

    [TestCase(ErpImportFileStatus.Completed)]
    [TestCase(ErpImportFileStatus.CompletedWithWarnings)]
    public async Task ProcessedRow_FinishedJustBeforeTheCutoff_IsDue_ButNotExactlyAtIt(ErpImportFileStatus status)
    {
        // Arrange
        var due = await GivenFileAsync(status, finishedAt: Cutoff - OneTick);
        await GivenFileAsync(status, finishedAt: Cutoff);
        await GivenFileAsync(status, finishedAt: Cutoff + OneTick);

        // Act
        var candidates = await ListAsync();

        // Assert
        candidates.ShouldHaveSingleItem().ShouldBe(new ErpImportFileRetentionCandidate(due.Id, due.StorageKey, false));
    }

    [Test]
    public async Task ProcessedRowWithoutFinishTime_CountsFromItsReceipt()
    {
        // Arrange
        var due = await GivenFileAsync(ErpImportFileStatus.Completed, finishedAt: null, receivedAt: Cutoff - OneTick);
        await GivenFileAsync(ErpImportFileStatus.Completed, finishedAt: null, receivedAt: Cutoff);

        // Act
        var candidates = await ListAsync();

        // Assert
        candidates.Select(c => c.Id).ShouldBe([due.Id]);
    }

    [Test]
    public async Task ProcessedRowFinishedRecently_IsNotDue_EvenWhenReceivedLongAgo()
    {
        // Arrange
        await GivenFileAsync(ErpImportFileStatus.Completed, finishedAt: Cutoff + OneTick, receivedAt: Cutoff.AddDays(-400));

        // Act + Assert
        (await ListAsync()).ShouldBeEmpty();
    }

    [TestCase(ErpImportFileStatus.Received)]
    [TestCase(ErpImportFileStatus.Importing)]
    [TestCase(ErpImportFileStatus.Failed)]
    [TestCase(ErpImportFileStatus.Interrupted)]
    public async Task UnfinishedOrFailedRow_IsNeverDue_HoweverOld(ErpImportFileStatus status)
    {
        // Arrange
        await GivenFileAsync(status, finishedAt: Cutoff.AddDays(-1000), receivedAt: Cutoff.AddDays(-1000));

        // Act + Assert
        (await ListAsync()).ShouldBeEmpty();
    }

    [TestCase(ErpImportFileStatus.Failed)]
    [TestCase(ErpImportFileStatus.Interrupted)]
    [TestCase(ErpImportFileStatus.Completed)]
    public async Task SoftDeletedRow_IsDueOnlyWhenDeletedBeforeTheCutoff(ErpImportFileStatus status)
    {
        // Arrange
        var due = await GivenFileAsync(status, finishedAt: Cutoff.AddDays(1), deletedTime: Cutoff - OneTick);
        await GivenFileAsync(status, finishedAt: Cutoff.AddDays(-10), deletedTime: Cutoff);
        await GivenFileAsync(status, finishedAt: Cutoff.AddDays(-10), deletedTime: Cutoff + OneTick);

        // Act
        var candidates = await ListAsync();

        // Assert
        candidates.ShouldHaveSingleItem().ShouldBe(new ErpImportFileRetentionCandidate(due.Id, due.StorageKey, true));
    }

    [Test]
    public async Task SoftDeletedRowWithoutDeletionTime_IsNeverDue()
    {
        // Arrange
        await GivenFileAsync(ErpImportFileStatus.Failed, finishedAt: Cutoff.AddDays(-500), isDeleted: true);

        // Act + Assert
        (await ListAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task RowsOfOtherDropPoints_AreIgnored()
    {
        // Arrange
        await GivenFileAsync(ErpImportFileStatus.Completed, finishedAt: Cutoff.AddDays(-5), dropPointId: Guid.NewGuid());

        // Act + Assert
        (await ListAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task DueRows_ComeOldestFirst_AndTheLimitCapsThem()
    {
        // Arrange
        var newest = await GivenFileAsync(ErpImportFileStatus.Completed, finishedAt: Cutoff.AddDays(-1));
        var oldest = await GivenFileAsync(ErpImportFileStatus.Failed, finishedAt: Cutoff.AddDays(-50), deletedTime: Cutoff.AddDays(-30));
        var middle = await GivenFileAsync(ErpImportFileStatus.CompletedWithWarnings, finishedAt: Cutoff.AddDays(-20));

        // Act
        var all = await ListAsync();
        var capped = await ListAsync(limit: 2);

        // Assert
        all.Select(c => c.Id).ShouldBe([oldest.Id, middle.Id, newest.Id]);
        capped.Select(c => c.Id).ShouldBe([oldest.Id, middle.Id]);
    }

    private async Task<List<ErpImportFileRetentionCandidate>> ListAsync(int limit = NoLimit)
    {
        await using var context = new DataBaseContext(_options, _httpAccessor);
        return await new ErpImportFileRepository(context).ListExpiredAsync(_dropPointId, Cutoff, limit);
    }

    private async Task<ErpImportFile> GivenFileAsync(
        ErpImportFileStatus status,
        DateTime? finishedAt,
        DateTime? receivedAt = null,
        DateTime? deletedTime = null,
        bool isDeleted = false,
        Guid? dropPointId = null)
    {
        var id = Guid.NewGuid();
        var file = new ErpImportFile
        {
            Id = id,
            DropPointId = dropPointId ?? _dropPointId,
            SourceSystemId = "erp-1",
            StorageKey = $"customer-1/{id:N}-orders.xml",
            FileName = "orders.xml",
            Sha256 = new string('a', 64),
            Status = status,
            ReceivedAt = receivedAt ?? (finishedAt ?? Cutoff).AddMinutes(-1),
            FinishedAt = finishedAt,
            IsDeleted = isDeleted || deletedTime != null,
            DeletedTime = deletedTime
        };

        await using var context = new DataBaseContext(_options, _httpAccessor);
        context.ErpImportFiles.Add(file);
        await context.SaveChangesAsync();
        return file;
    }
}
