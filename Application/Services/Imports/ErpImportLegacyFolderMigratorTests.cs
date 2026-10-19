// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportLegacyFolderMigrator over substituted storage and repositories: folder-to-status
/// mapping with the failure reasons, the deterministic target key (upload names keep their original inbox
/// key, ERP names get an id derived from the legacy key), the row being committed before the move, the
/// update of an existing row, an Importing row being left alone, the deterministic collision key, keys outside the
/// three legacy folders being ignored, and the linked exceptions: resolved for a Completed (processed/) row because they
/// are stale, left open for Failed and Interrupted rows.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Services.Imports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportLegacyFolderMigratorTests
{
    private const string Prefix = "erp/orders/";
    private const string UploadId = "0123456789abcdef0123456789abcdef";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTime WrittenAt = new(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    private static readonly ErpDropPoint DropPoint = new()
    {
        Id = Guid.NewGuid(),
        Name = "Default",
        SourceSystemId = "erp-1",
        BucketPrefix = "erp/orders",
        IsEnabled = false
    };

    private IObjectStorageService _storage = null!;
    private IErpImportFileRepository _fileRepository = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private List<ErpImportFile> _added = null!;
    private ErpImportLegacyFolderMigrator _migrator = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _added = [];

        _fileRepository.Add(Arg.Do<ErpImportFile>(f => _added.Add(f)));
        _fileRepository.GetByStorageKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new List<ErpImportFile>());
        _exceptionRepository.GetByFileKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns(new List<ErpImportException>());
        _storage.GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((StoredObjectInfo?)null);
        _storage.TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _migrator = new ErpImportLegacyFolderMigrator(
            _storage, _fileRepository, _exceptionRepository, _unitOfWork, new SettableTimeProvider(Now),
            NullLogger<ErpImportLegacyFolderMigrator>.Instance);
    }

    [Test]
    public async Task MigrateAsync_ProcessedUpload_BecomesCompletedAtItsOriginalInboxKeyAndIsMovedAfterTheCommit()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);

        // Act
        var migrated = await _migrator.MigrateAsync(DropPoint);

        // Assert
        migrated.ShouldBe(1);
        var row = _added.ShouldHaveSingleItem();
        row.StorageKey.ShouldBe($"{Prefix}{UploadId}-orders.xml");
        row.FileName.ShouldBe("orders.xml");
        row.Status.ShouldBe(ErpImportFileStatus.Completed);
        row.FailureReason.ShouldBeNull();
        row.TotalOrders.ShouldBeNull("the counts of a pre-ledger import are unknown");
        row.Channel.ShouldBe(ErpImportChannel.InboxFolder);
        row.ReceivedAt.ShouldBe(WrittenAt);
        row.FinishedAt.ShouldBe(WrittenAt);
        row.Sha256.ShouldBe(Sha);
        row.DropPointId.ShouldBe(DropPoint.Id);
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _exceptionRepository.LinkToImportFileAsync($"{Prefix}{UploadId}-orders.xml", row.Id, Arg.Any<CancellationToken>());
            _storage.TryMoveAsync(legacyKey, row.StorageKey, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task MigrateAsync_ErpNamedFile_GetsTheSameDerivedKeyOnEveryRun()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/orders.xml";
        GivenLegacyObjects(legacyKey);

        // Act
        await _migrator.MigrateAsync(DropPoint);
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        _added.Count.ShouldBe(2);
        _added[0].StorageKey.ShouldBe(_added[1].StorageKey, "a rerun after a crash must find the row by the same key");
        var storedName = _added[0].StorageKey[Prefix.Length..];
        ErpImportStorageKeys.HasUploadIdPrefix(storedName).ShouldBeTrue();
        ErpImportStorageKeys.ToDisplayFileName(storedName).ShouldBe("orders.xml");
    }

    [Test]
    public async Task MigrateAsync_ErrorFile_BecomesFailedWithTheLatestRecordedReason()
    {
        // Arrange
        GivenLegacyObjects($"{Prefix}error/{UploadId}-broken.xml");
        _exceptionRepository.GetByFileKeysAsync(
                Arg.Is<IReadOnlyCollection<string>>(keys => keys.Single() == $"{Prefix}{UploadId}-broken.xml"),
                Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportException>
            {
                new() { Reason = "Malformed XML (latest)" },
                new() { Reason = "older reason" }
            });

        // Act
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        var row = _added.ShouldHaveSingleItem();
        row.Status.ShouldBe(ErpImportFileStatus.Failed);
        row.FailureReason.ShouldBe("Malformed XML (latest)");
    }

    [Test]
    public async Task MigrateAsync_ErrorFileWithoutException_AndProcessingFile_GetTheirLegacyReasons()
    {
        // Arrange
        GivenLegacyObjects($"{Prefix}error/{UploadId}-a.xml", $"{Prefix}processing/{UploadId}-b.xml");

        // Act
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        var failed = _added.Single(r => r.FileName == "a.xml");
        failed.Status.ShouldBe(ErpImportFileStatus.Failed);
        failed.FailureReason.ShouldBe(ErpImportFileFailureReasons.LegacyErrorWithoutReason);
        var interrupted = _added.Single(r => r.FileName == "b.xml");
        interrupted.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        interrupted.FailureReason.ShouldBe(ErpImportFileFailureReasons.LegacyImportInterrupted);
    }

    [Test]
    public async Task MigrateAsync_RowAlreadyRegisteredForTheOriginalKey_IsUpdatedInsteadOfInserted()
    {
        // Arrange
        var legacyKey = $"{Prefix}error/{UploadId}-orders.xml";
        var originalKey = $"{Prefix}{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);
        var existing = new ErpImportFile { Id = Guid.NewGuid(), StorageKey = originalKey, Status = ErpImportFileStatus.Received };
        _fileRepository.GetByStorageKeysAsync(Arg.Is<IReadOnlyCollection<string>>(keys => keys.Single() == originalKey), Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportFile> { existing });
        _fileRepository.TryApplyLegacyStateAsync(existing.Id, originalKey, ErpImportFileStatus.Failed, Arg.Any<string?>(), WrittenAt, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var migrated = await _migrator.MigrateAsync(DropPoint);

        // Assert
        migrated.ShouldBe(1);
        _added.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
        await _exceptionRepository.Received(1).LinkToImportFileAsync(originalKey, existing.Id, Arg.Any<CancellationToken>());
        await _storage.Received(1).TryMoveAsync(legacyKey, originalKey, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MigrateAsync_ExistingRowBeingImported_IsLeftForTheNextRun()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);
        var existing = new ErpImportFile { Id = Guid.NewGuid(), StorageKey = $"{Prefix}{UploadId}-orders.xml", Status = ErpImportFileStatus.Importing };
        _fileRepository.GetByStorageKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportFile> { existing });
        _fileRepository.TryApplyLegacyStateAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ErpImportFileStatus>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var migrated = await _migrator.MigrateAsync(DropPoint);

        // Assert
        migrated.ShouldBe(0);
        await _storage.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MigrateAsync_TargetKeyOccupied_UsesASecondDerivedKeyThatIsStableAcrossRuns()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/{UploadId}-orders.xml";
        var originalKey = $"{Prefix}{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);
        _storage.GetObjectInfoAsync(originalKey, Arg.Any<CancellationToken>()).Returns(new StoredObjectInfo(1, Sha));
        _storage.TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        await _migrator.MigrateAsync(DropPoint);
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        _added.Count.ShouldBe(2, "the substituted repository never finds the first row, so both runs insert");
        var collisionKey = _added[0].StorageKey;
        collisionKey.ShouldNotBe(originalKey);
        _added[1].StorageKey.ShouldBe(collisionKey, "a failed move is retried under the same key, so a real ledger finds the row again");
        ErpImportStorageKeys.HasUploadIdPrefix(collisionKey[Prefix.Length..]).ShouldBeTrue();
        ErpImportStorageKeys.ToDisplayFileName(collisionKey[Prefix.Length..]).ShouldBe("orders.xml");
        await _storage.Received(2).TryMoveAsync(legacyKey, collisionKey, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MigrateAsync_BothTargetKeysOccupied_LeavesTheFileInPlace()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);
        _storage.GetObjectInfoAsync(Arg.Is<string>(key => key != legacyKey), Arg.Any<CancellationToken>()).Returns(new StoredObjectInfo(1, Sha));

        // Act
        var migrated = await _migrator.MigrateAsync(DropPoint);

        // Assert
        migrated.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _storage.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MigrateAsync_InboxFilesAndDeeperOrForeignKeys_AreIgnored()
    {
        // Arrange
        GivenLegacyObjects(
            $"{Prefix}{UploadId}-inbox.xml",
            $"{Prefix}processed/nested/deeper.xml",
            $"{Prefix}archive/old.xml");

        // Act
        var migrated = await _migrator.MigrateAsync(DropPoint);

        // Assert
        migrated.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _storage.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MigrateAsync_OneFileFailing_DiscardsTrackedChangesAndMigratesTheNext()
    {
        // Arrange
        var failingKey = $"{Prefix}processed/{UploadId}-a.xml";
        GivenLegacyObjects(failingKey, $"{Prefix}processed/{UploadId}-b.xml");
        _storage.TryMoveAsync(failingKey, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk gone"));

        // Act
        var migrated = await _migrator.MigrateAsync(DropPoint);

        // Assert
        migrated.ShouldBe(1);
        _unitOfWork.Received(1).DiscardTrackedChanges();
        await _storage.Received(1).TryMoveAsync($"{Prefix}processed/{UploadId}-b.xml", $"{Prefix}{UploadId}-b.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MigrateAsync_ProcessedFile_LinksAndResolvesTheStaleOpenExceptionsOfItsKey()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/{UploadId}-orders.xml";
        var originalKey = $"{Prefix}{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);

        // Act
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        var row = _added.ShouldHaveSingleItem();
        Received.InOrder(() =>
        {
            _exceptionRepository.LinkToImportFileAsync(originalKey, row.Id, Arg.Any<CancellationToken>());
            _exceptionRepository.ResolveOpenForImportFileAsync(row.Id, Now, Arg.Any<CancellationToken>());
            _storage.TryMoveAsync(legacyKey, originalKey, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task MigrateAsync_ProcessedFileUpdatingAnExistingRow_ResolvesTheExceptionsOfThatRow()
    {
        // Arrange
        var legacyKey = $"{Prefix}processed/{UploadId}-orders.xml";
        var originalKey = $"{Prefix}{UploadId}-orders.xml";
        GivenLegacyObjects(legacyKey);
        var existing = new ErpImportFile { Id = Guid.NewGuid(), StorageKey = originalKey, Status = ErpImportFileStatus.Received };
        _fileRepository.GetByStorageKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportFile> { existing });
        _fileRepository.TryApplyLegacyStateAsync(existing.Id, originalKey, ErpImportFileStatus.Completed, null, WrittenAt, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        await _exceptionRepository.Received(1).LinkToImportFileAsync(originalKey, existing.Id, Arg.Any<CancellationToken>());
        await _exceptionRepository.Received(1).ResolveOpenForImportFileAsync(existing.Id, Now, Arg.Any<CancellationToken>());
    }

    [TestCase("error")]
    [TestCase("processing")]
    public async Task MigrateAsync_ErrorOrProcessingFile_LinksItsExceptionsWithoutResolvingThem(string folder)
    {
        // Arrange
        GivenLegacyObjects($"{Prefix}{folder}/{UploadId}-orders.xml");

        // Act
        await _migrator.MigrateAsync(DropPoint);

        // Assert
        var row = _added.ShouldHaveSingleItem();
        await _exceptionRepository.Received(1).LinkToImportFileAsync($"{Prefix}{UploadId}-orders.xml", row.Id, Arg.Any<CancellationToken>());
        await _exceptionRepository.DidNotReceive().ResolveOpenForImportFileAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private void GivenLegacyObjects(params string[] keys)
    {
        _storage.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>())
            .Returns(keys.Select(key => new StorageObjectMetadata(key, 1, WrittenAt)).ToList());
        foreach (var key in keys)
        {
            _storage.GetObjectInfoAsync(key, Arg.Any<CancellationToken>()).Returns(new StoredObjectInfo(1, Sha));
        }
    }
}
