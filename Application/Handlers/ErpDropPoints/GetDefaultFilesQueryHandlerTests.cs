// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the table-only file listing of the default drop point: grouping of ledger rows into
/// Inbox/Processed/Failed, stale Importing rows reported as Interrupted under Failed, the processed cap passed
/// to the repository, rejected orders attached per file, and the duplicate hint resolved also for an original
/// outside the listed rows. The object storage is never consulted.
/// </summary>

using Klacks.Api.Application.Handlers.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.ErpDropPoints;

[TestFixture]
public class GetDefaultFilesQueryHandlerTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid DropPointId = Guid.NewGuid();

    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IErpImportFileRepository _fileRepository = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private List<ErpImportFile> _rows = null!;
    private List<ErpImportException> _openRejections = null!;
    private GetDefaultFilesQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _rows = [];
        _openRejections = [];

        _defaultDropPointProvider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new ErpDropPoint { Id = DropPointId, Name = "Default", SourceSystemId = "default", BucketPrefix = "erp/orders" });
        _fileRepository.ListForDropPointAsync(DropPointId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => _rows.ToList());
        _fileRepository.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<ErpImportFile>());
        _exceptionRepository.GetOpenRejectedOrdersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _openRejections.ToList());

        _handler = new GetDefaultFilesQueryHandler(_defaultDropPointProvider, _fileRepository, _exceptionRepository, new SettableTimeProvider(Now));
    }

    [Test]
    public async Task Handle_GroupsLedgerRowsIntoInboxProcessedAndFailed()
    {
        // Arrange
        var received = GivenRow(ErpImportFileStatus.Received);
        var importing = GivenRow(ErpImportFileStatus.Importing, f => f.HeartbeatAt = Now.AddSeconds(-20));
        var completed = GivenRow(ErpImportFileStatus.Completed);
        var warnings = GivenRow(ErpImportFileStatus.CompletedWithWarnings);
        var failed = GivenRow(ErpImportFileStatus.Failed, f => f.FailureReason = "Malformed XML");
        var interrupted = GivenRow(ErpImportFileStatus.Interrupted, f => f.FailureReason = ErpImportFileFailureReasons.StaleHeartbeat);

        // Act
        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        // Assert
        result.Inbox.Select(f => f.Id).ShouldBe([received.Id, importing.Id]);
        result.Processed.Select(f => f.Id).ShouldBe([completed.Id, warnings.Id]);
        result.Failed.Select(f => f.Id).ShouldBe([failed.Id, interrupted.Id]);
        result.Failed.Single(f => f.Id == failed.Id).FailureReason.ShouldBe("Malformed XML");
        await _fileRepository.Received(1).ListForDropPointAsync(DropPointId, ErpImportFileTiming.ProcessedListingCap, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ImportingRowWithStaleHeartbeat_IsReportedAsInterruptedUnderFailed()
    {
        // Arrange
        var stale = GivenRow(ErpImportFileStatus.Importing, f =>
        {
            f.StartedAt = Now.AddMinutes(-30);
            f.HeartbeatAt = Now - ErpImportFileTiming.StaleAfter - TimeSpan.FromSeconds(1);
            f.ProcessedOrders = 350;
            f.TotalOrders = 620;
        });

        // Act
        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        // Assert
        result.Inbox.ShouldBeEmpty();
        var listed = result.Failed.ShouldHaveSingleItem();
        listed.Id.ShouldBe(stale.Id);
        listed.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        listed.FailureReason.ShouldBe(ErpImportFileFailureReasons.StaleHeartbeat);
        listed.ProcessedOrders.ShouldBe(350);
        listed.TotalOrders.ShouldBe(620);
        await _fileRepository.DidNotReceive().ReapStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ImportingRowWithLiveHeartbeat_StaysInTheInboxWithItsProgress()
    {
        // Arrange
        GivenRow(ErpImportFileStatus.Importing, f =>
        {
            f.HeartbeatAt = Now - ErpImportFileTiming.StaleAfter + TimeSpan.FromSeconds(1);
            f.ProcessedOrders = 350;
            f.TotalOrders = 620;
        });

        // Act
        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        // Assert
        var listed = result.Inbox.ShouldHaveSingleItem();
        listed.Status.ShouldBe(ErpImportFileStatus.Importing);
        listed.ProcessedOrders.ShouldBe(350);
        listed.TotalOrders.ShouldBe(620);
    }

    [Test]
    public async Task Handle_MapsLedgerFieldsAndAttachesRejectedOrdersPerFile()
    {
        // Arrange
        var warnings = GivenRow(ErpImportFileStatus.CompletedWithWarnings, f =>
        {
            f.FileName = "orders.xml";
            f.SizeBytes = 1234;
            f.Channel = ErpImportChannel.TokenPush;
            f.UploadedBy = "ERP token";
            f.StartedAt = Now.AddMinutes(-2);
            f.FinishedAt = Now.AddMinutes(-1);
            f.TotalOrders = 5;
            f.ProcessedOrders = 5;
            f.NewCount = 1;
            f.UpdatedCount = 1;
            f.UnchangedCount = 1;
            f.SupersededCount = 1;
            f.RejectedCount = 1;
        });
        var other = GivenRow(ErpImportFileStatus.Completed);
        _openRejections.Add(new ErpImportException { ImportFileId = warnings.Id, ExternalOrderReference = "ORD-7", Reason = "No weekday" });

        // Act
        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        // Assert
        var listed = result.Processed.Single(f => f.Id == warnings.Id);
        listed.FileName.ShouldBe("orders.xml");
        listed.SizeBytes.ShouldBe(1234);
        listed.Channel.ShouldBe(ErpImportChannel.TokenPush);
        listed.UploadedBy.ShouldBe("ERP token");
        listed.ReceivedAt.ShouldBe(warnings.ReceivedAt);
        listed.StartedAt.ShouldBe(Now.AddMinutes(-2));
        listed.FinishedAt.ShouldBe(Now.AddMinutes(-1));
        listed.TotalOrders.ShouldBe(5);
        new[] { listed.ProcessedOrders, listed.NewCount, listed.UpdatedCount, listed.UnchangedCount, listed.SupersededCount, listed.RejectedCount }
            .ShouldBe(new[] { 5, 1, 1, 1, 1, 1 });
        var rejected = listed.RejectedOrders.ShouldHaveSingleItem();
        rejected.ExternalOrderReference.ShouldBe("ORD-7");
        rejected.Reason.ShouldBe("No weekday");
        result.Processed.Single(f => f.Id == other.Id).RejectedOrders.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_DuplicateHint_UsesTheOriginalsReceiptTimeAlsoWhenTheOriginalIsNotListed()
    {
        // Arrange
        var listedOriginal = GivenRow(ErpImportFileStatus.Completed);
        var duplicateOfListed = GivenRow(ErpImportFileStatus.Received, f => f.DuplicateOfFileId = listedOriginal.Id);
        var unlistedOriginal = new ErpImportFile { Id = Guid.NewGuid(), ReceivedAt = Now.AddDays(-40) };
        var duplicateOfUnlisted = GivenRow(ErpImportFileStatus.Received, f => f.DuplicateOfFileId = unlistedOriginal.Id);
        _fileRepository.GetByIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { unlistedOriginal.Id })),
                Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportFile> { unlistedOriginal });

        // Act
        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        // Assert
        result.Inbox.Single(f => f.Id == duplicateOfListed.Id).DuplicateOfReceivedAt.ShouldBe(listedOriginal.ReceivedAt);
        result.Inbox.Single(f => f.Id == duplicateOfUnlisted.Id).DuplicateOfReceivedAt.ShouldBe(Now.AddDays(-40));
        result.Processed.Single().DuplicateOfReceivedAt.ShouldBeNull();
    }

    private ErpImportFile GivenRow(ErpImportFileStatus status, Action<ErpImportFile>? configure = null)
    {
        var row = new ErpImportFile
        {
            Id = Guid.NewGuid(),
            DropPointId = DropPointId,
            FileName = $"file-{_rows.Count}.xml",
            Status = status,
            ReceivedAt = Now.AddMinutes(-(_rows.Count + 1)),
            Attempt = status == ErpImportFileStatus.Received ? 0 : 1
        };
        if (status == ErpImportFileStatus.Importing)
        {
            row.StartedAt = Now.AddMinutes(-1);
            row.HeartbeatAt = Now.AddSeconds(-5);
        }

        configure?.Invoke(row);
        _rows.Add(row);
        return row;
    }
}
