// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportRetentionService over substituted repositories and storage: the cutoff is now minus
/// ErpImportFileTiming.RetentionPeriod and the batch size is passed as the query limit; an expired processed row
/// deletes its stored object BEFORE the conditional row delete and removes its linked exceptions in the same
/// transaction; a failing object delete keeps the row (and the next candidate still runs); a row that changed
/// meanwhile keeps its exceptions; a soft-deleted row is only purged when its object is absent and its object is
/// never deleted here; an unexpected error of one row does not stop the others; cancellation stops the loop.
/// Which rows are due (boundary, Failed/Interrupted never listed) is covered by the repository query tests and
/// the PostgreSQL integration tests.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportRetentionServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpectedCutoff = Now - ErpImportFileTiming.RetentionPeriod;

    private static readonly ErpDropPoint DropPoint = new()
    {
        Id = Guid.NewGuid(),
        Name = "Test ERP",
        SourceSystemId = "erp-1",
        BucketPrefix = "customer-1",
        IsEnabled = true
    };

    private IErpImportFileRepository _fileRepository = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private List<ErpImportFileRetentionCandidate> _candidates = null!;
    private ErpImportRetentionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _candidates = [];

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _fileRepository.ListExpiredAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => _candidates.ToList());
        _fileRepository.TryDeleteExpiredProcessedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _fileRepository.TryPurgeExpiredDeletedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _exceptionRepository.GetIdsByImportFileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ => new List<Guid>());
        _objectStorageService.GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((StoredObjectInfo?)null);

        _service = new ErpImportRetentionService(
            _fileRepository, _exceptionRepository, _objectStorageService, _unitOfWork,
            new SettableTimeProvider(Now), NullLogger<ErpImportRetentionService>.Instance);
    }

    [Test]
    public async Task PurgeExpiredAsync_QueriesWithNowMinusRetentionPeriodAndTheBatchSize()
    {
        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(0);
        ExpectedCutoff.ShouldBe(Now.AddDays(-90));
        await _fileRepository.Received(1).ListExpiredAsync(DropPoint.Id, ExpectedCutoff, ErpImportFileTiming.RetentionBatchSize, Arg.Any<CancellationToken>());
        ErpImportFileTiming.RetentionBatchSize.ShouldBe(200);
    }

    [Test]
    public async Task PurgeExpiredAsync_NothingDue_TouchesNeitherStorageNorRows()
    {
        // Act
        await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().TryDeleteExpiredProcessedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().TryPurgeExpiredDeletedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PurgeExpiredAsync_ExpiredProcessedRow_DeletesObjectFirstThenRowAndItsExceptions()
    {
        // Arrange
        var candidate = GivenCandidate(isDeleted: false);
        var exceptionIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        _exceptionRepository.GetIdsByImportFileAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(exceptionIds);

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(1);
        Received.InOrder(() =>
        {
            _objectStorageService.DeleteAsync(candidate.StorageKey, Arg.Any<CancellationToken>());
            _exceptionRepository.GetIdsByImportFileAsync(candidate.Id, Arg.Any<CancellationToken>());
            _fileRepository.TryDeleteExpiredProcessedAsync(candidate.Id, ExpectedCutoff, Arg.Any<CancellationToken>());
            _exceptionRepository.DeleteByIdsAsync(exceptionIds, Arg.Any<CancellationToken>());
        });
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        await _objectStorageService.DidNotReceive().GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PurgeExpiredAsync_ObjectDeleteFails_KeepsTheRowAndContinuesWithTheNextFile()
    {
        // Arrange
        var failing = GivenCandidate(isDeleted: false);
        var next = GivenCandidate(isDeleted: false);
        _objectStorageService.DeleteAsync(failing.StorageKey, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("locked"));

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(1);
        await _fileRepository.DidNotReceive().TryDeleteExpiredProcessedAsync(failing.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _exceptionRepository.DidNotReceive().GetIdsByImportFileAsync(failing.Id, Arg.Any<CancellationToken>());
        await _fileRepository.Received(1).TryDeleteExpiredProcessedAsync(next.Id, ExpectedCutoff, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PurgeExpiredAsync_RowChangedMeanwhile_KeepsItsExceptionsAndCountsNothing()
    {
        // Arrange
        var candidate = GivenCandidate(isDeleted: false);
        _exceptionRepository.GetIdsByImportFileAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns([Guid.NewGuid()]);
        _fileRepository.TryDeleteExpiredProcessedAsync(candidate.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(0);
        await _exceptionRepository.DidNotReceive().DeleteByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PurgeExpiredAsync_SoftDeletedRowWithoutObject_PurgesTheRowWithoutDeletingAnyObject()
    {
        // Arrange
        var candidate = GivenCandidate(isDeleted: true);
        var exceptionIds = new List<Guid> { Guid.NewGuid() };
        _exceptionRepository.GetIdsByImportFileAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(exceptionIds);

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(1);
        Received.InOrder(() =>
        {
            _objectStorageService.GetObjectInfoAsync(candidate.StorageKey, Arg.Any<CancellationToken>());
            _exceptionRepository.GetIdsByImportFileAsync(candidate.Id, Arg.Any<CancellationToken>());
            _fileRepository.TryPurgeExpiredDeletedAsync(candidate.Id, ExpectedCutoff, Arg.Any<CancellationToken>());
            _exceptionRepository.DeleteByIdsAsync(exceptionIds, Arg.Any<CancellationToken>());
        });
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().TryDeleteExpiredProcessedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PurgeExpiredAsync_SoftDeletedRowWhoseObjectStillExists_KeepsRowAndObject()
    {
        // Arrange
        var candidate = GivenCandidate(isDeleted: true);
        _objectStorageService.GetObjectInfoAsync(candidate.StorageKey, Arg.Any<CancellationToken>())
            .Returns(new StoredObjectInfo(10, new string('a', ErpImportFileLimits.Sha256HexLength), Now));

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(0);
        await _fileRepository.DidNotReceive().TryPurgeExpiredDeletedAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
    }

    [Test]
    public async Task PurgeExpiredAsync_UnexpectedErrorOnOneRow_ContinuesWithTheOthers()
    {
        // Arrange
        var broken = GivenCandidate(isDeleted: true);
        var next = GivenCandidate(isDeleted: false);
        _objectStorageService.GetObjectInfoAsync(broken.StorageKey, Arg.Any<CancellationToken>()).ThrowsAsync(new UnauthorizedAccessException("denied"));

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(1);
        await _fileRepository.DidNotReceive().TryPurgeExpiredDeletedAsync(broken.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _fileRepository.Received(1).TryDeleteExpiredProcessedAsync(next.Id, ExpectedCutoff, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PurgeExpiredAsync_ProcessesEveryReturnedCandidate()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
        {
            GivenCandidate(isDeleted: i % 2 == 0);
        }

        // Act
        var removed = await _service.PurgeExpiredAsync(DropPoint);

        // Assert
        removed.ShouldBe(5);
    }

    [Test]
    public async Task PurgeExpiredAsync_Cancelled_StopsBeforeTheNextFile()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var first = GivenCandidate(isDeleted: false);
        var second = GivenCandidate(isDeleted: false);
        _objectStorageService.DeleteAsync(first.StorageKey, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            });

        // Act + Assert
        await Should.ThrowAsync<OperationCanceledException>(() => _service.PurgeExpiredAsync(DropPoint, cancellation.Token));
        await _objectStorageService.DidNotReceive().DeleteAsync(second.StorageKey, Arg.Any<CancellationToken>());
    }

    private ErpImportFileRetentionCandidate GivenCandidate(bool isDeleted)
    {
        var id = Guid.NewGuid();
        var candidate = new ErpImportFileRetentionCandidate(id, $"customer-1/{id:N}-orders.xml", isDeleted);
        _candidates.Add(candidate);
        return candidate;
    }
}
