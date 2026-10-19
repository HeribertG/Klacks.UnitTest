// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportInboxRegistrationService: a file the ERP dropped under its own name is hashed under
/// that name first, then renamed to a unique "{guid}-{name}" key (atomic TryMoveAsync) and registered there as a
/// Received InboxFolder row without uploader, received at the object's last write time (so the runner's
/// oldest-first order follows the delivery order); a same-name re-delivery after the import is registered again;
/// a key that already carries the registration id but has no row (lost ledger, e.g. after a database restore) is
/// registered as Failed with LedgerEntryMissing and never queued; a key with a soft-deleted row is not registered
/// and its leftover object is deleted; keys with a live row are skipped without hashing; a lost rename is skipped;
/// objects in sub-folders and other prefixes are ignored; a vanished file is skipped; a key that would exceed the
/// column length is skipped; one unreadable file does not stop the others; and a concurrent registration
/// (duplicate key on commit) detaches that row and carries on. Uses the real ErpImportFileRegistrar over
/// substituted repository/UoW.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportInboxRegistrationServiceTests
{
    private const string BucketPrefix = "erp/orders";
    private const string Prefix = "erp/orders/";
    private const string UploadId = "0123456789abcdef0123456789abcdef";
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Older = new(2026, 10, 18, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 10, 19, 6, 0, 0, DateTimeKind.Utc);

    private IObjectStorageService _storage = null!;
    private IErpImportFileRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ErpImportInboxRegistrationService _service = null!;
    private ErpDropPoint _dropPoint = null!;
    private List<ErpImportFile> _added = null!;
    private Dictionary<string, string> _renamedTo = null!;
    private Dictionary<string, StoredObjectInfo> _objects = null!;

    [SetUp]
    public void SetUp()
    {
        _storage = Substitute.For<IObjectStorageService>();
        _repository = Substitute.For<IErpImportFileRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _added = [];
        _renamedTo = new Dictionary<string, string>(StringComparer.Ordinal);
        _objects = new Dictionary<string, StoredObjectInfo>(StringComparer.Ordinal);

        _repository.Add(Arg.Do<ErpImportFile>(file => _added.Add(file)));
        GivenRows();

        _storage.TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _renamedTo[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1);
                return true;
            });
        _storage.GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ObjectAt(ci.ArgAt<string>(0)));

        var registrar = new ErpImportFileRegistrar(
            _repository, _unitOfWork, new SettableTimeProvider(Now), NullLogger<ErpImportFileRegistrar>.Instance);
        _service = new ErpImportInboxRegistrationService(
            _storage, _repository, registrar, _unitOfWork, NullLogger<ErpImportInboxRegistrationService>.Instance);
        _dropPoint = new ErpDropPoint { Id = Guid.NewGuid(), SourceSystemId = "erp-1", BucketPrefix = BucketPrefix };
    }

    [Test]
    public async Task RegisterInboxFilesAsync_ErpNamedFile_IsHashedThenRenamedToAUniqueKeyAndRegisteredAsReceived()
    {
        // Arrange
        var key = Prefix + "orders.xml";
        GivenListing(key);
        GivenObject(key, new StoredObjectInfo(42, ShaA, Older));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(1);
        var renamed = _renamedTo[key];
        renamed.ShouldMatch("^" + Regex.Escape(Prefix) + "[0-9a-f]{32}-orders\\.xml$");
        Received.InOrder(() =>
        {
            _storage.GetObjectInfoAsync(key, Arg.Any<CancellationToken>());
            _storage.TryMoveAsync(key, renamed, Arg.Any<CancellationToken>());
        });
        var file = _added.ShouldHaveSingleItem();
        file.StorageKey.ShouldBe(renamed);
        file.FileName.ShouldBe("orders.xml");
        file.Channel.ShouldBe(ErpImportChannel.InboxFolder);
        file.UploadedBy.ShouldBeNull();
        file.SizeBytes.ShouldBe(42);
        file.Sha256.ShouldBe(ShaA);
        file.Status.ShouldBe(ErpImportFileStatus.Received);
        file.FailureReason.ShouldBeNull();
        file.FinishedAt.ShouldBeNull();
        file.DropPointId.ShouldBe(_dropPoint.Id);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_ErpNamedFiles_AreReceivedAtTheirLastWriteTimeWhateverTheKeyOrder()
    {
        // Arrange
        var newerFirstByName = Prefix + "a-orders.xml";
        var olderSecondByName = Prefix + "b-orders.xml";
        GivenListing(newerFirstByName, olderSecondByName);
        GivenObject(newerFirstByName, new StoredObjectInfo(1, ShaA, Newer));
        GivenObject(olderSecondByName, new StoredObjectInfo(2, ShaB, Older));

        // Act
        await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        _added.Count.ShouldBe(2);
        _added.Single(f => f.Sha256 == ShaA).ReceivedAt.ShouldBe(Newer);
        _added.Single(f => f.Sha256 == ShaB).ReceivedAt.ShouldBe(Older);
        _added.ShouldAllBe(f => f.ReceivedAt.Kind == DateTimeKind.Utc);
        _added.OrderBy(f => f.ReceivedAt).Select(f => f.Sha256).ShouldBe(new[] { ShaB, ShaA });
    }

    [Test]
    public async Task RegisterInboxFilesAsync_ObjectWithoutWriteTime_IsReceivedNow()
    {
        // Arrange
        var key = Prefix + "orders.xml";
        GivenListing(key);
        GivenObject(key, new StoredObjectInfo(42, ShaA));

        // Act
        await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        _added.ShouldHaveSingleItem().ReceivedAt.ShouldBe(Now);
    }

    [Test]
    public async Task RegisterInboxFilesAsync_RegistrationShapedKeyWithoutAnyRow_IsRegisteredFailedAsLedgerEntryMissing()
    {
        // Arrange
        var key = Prefix + UploadId + "-orders.xml";
        GivenListing(key);
        GivenObject(key, new StoredObjectInfo(42, ShaA, Older));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(1);
        await _storage.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        var file = _added.ShouldHaveSingleItem();
        file.StorageKey.ShouldBe(key);
        file.FileName.ShouldBe("orders.xml");
        file.Status.ShouldBe(ErpImportFileStatus.Failed);
        file.FailureReason.ShouldBe(ErpImportFileFailureReasons.LedgerEntryMissing);
        file.Sha256.ShouldBe(ShaA);
        file.SizeBytes.ShouldBe(42);
        file.ReceivedAt.ShouldBe(Older);
        file.FinishedAt.ShouldBe(Now);
        file.Attempt.ShouldBe(0);
        file.Channel.ShouldBe(ErpImportChannel.InboxFolder);
    }

    [Test]
    public async Task RegisterInboxFilesAsync_KeyWithOnlyASoftDeletedRow_IsNotRegisteredAndItsLeftoverIsDeleted()
    {
        // Arrange
        var key = Prefix + UploadId + "-orders.xml";
        GivenListing(key);
        GivenObject(key, new StoredObjectInfo(42, ShaA, Older));
        GivenRows(new ErpImportFileKeyState(key, IsDeleted: true));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _storage.Received(1).DeleteAsync(key, Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_KeyWithALiveAndASoftDeletedRow_IsSkippedAndKept()
    {
        // Arrange
        var key = Prefix + UploadId + "-orders.xml";
        GivenListing(key);
        GivenRows(new ErpImportFileKeyState(key, IsDeleted: true), new ErpImportFileKeyState(key, IsDeleted: false));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterInboxFilesAsync_SameNameRedeliveredAfterRegistration_IsRegisteredAgainUnderANewKey()
    {
        // Arrange
        var erpKey = Prefix + "orders.xml";
        GivenListing(erpKey);
        GivenObject(erpKey, new StoredObjectInfo(10, ShaA, Older));
        await _service.RegisterInboxFilesAsync(_dropPoint);
        var firstKey = _added.ShouldHaveSingleItem().StorageKey;

        _renamedTo.Clear();
        GivenListing(firstKey, erpKey);
        GivenObject(erpKey, new StoredObjectInfo(20, ShaB, Newer));
        GivenRows(new ErpImportFileKeyState(firstKey, IsDeleted: false));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(1);
        _added.Count.ShouldBe(2);
        var second = _added[1];
        second.StorageKey.ShouldNotBe(firstKey);
        second.FileName.ShouldBe("orders.xml");
        second.Sha256.ShouldBe(ShaB);
        second.Status.ShouldBe(ErpImportFileStatus.Received);
    }

    [Test]
    public async Task RegisterInboxFilesAsync_RenameLostToAnotherCaller_IsSkipped()
    {
        // Arrange
        var key = Prefix + "orders.xml";
        GivenListing(key);
        GivenObject(key, new StoredObjectInfo(42, ShaA, Older));
        _storage.TryMoveAsync(key, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_UnreadableFile_KeepsItsNameAndDoesNotStopTheNextFile()
    {
        // Arrange
        var unreadable = Prefix + "a-locked.xml";
        var healthy = Prefix + "b-orders.xml";
        GivenListing(unreadable, healthy);
        GivenObject(healthy, new StoredObjectInfo(2, ShaB, Older));
        _storage.GetObjectInfoAsync(unreadable, Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedAccessException("locked"));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(1);
        _added.ShouldHaveSingleItem().Sha256.ShouldBe(ShaB);
        _renamedTo.ShouldNotContainKey(unreadable);
        _unitOfWork.Received(1).DiscardTrackedChanges();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_Cancelled_StopsInsteadOfSwallowingTheCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var key = Prefix + "orders.xml";
        GivenListing(key);
        _storage.GetObjectInfoAsync(key, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        _storage.When(s => s.GetObjectInfoAsync(key, Arg.Any<CancellationToken>())).Do(_ => cancellation.Cancel());

        // Act + Assert
        await Should.ThrowAsync<OperationCanceledException>(() => _service.RegisterInboxFilesAsync(_dropPoint, cancellation.Token));
        _unitOfWork.DidNotReceive().DiscardTrackedChanges();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_RenamedKeyWouldExceedTheColumnLength_IsSkippedWithoutMoving()
    {
        // Arrange
        var longName = new string('n', ErpImportFileLimits.StorageKeyMaxLength - Prefix.Length - 10) + ".xml";
        GivenListing(Prefix + longName);

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(0);
        await _storage.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _added.ShouldBeEmpty();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_KeyWithARow_IsSkippedWithoutHashing()
    {
        // Arrange
        var known = Prefix + UploadId + "-known.xml";
        var fresh = Prefix + "fresh.xml";
        GivenListing(known, fresh);
        GivenRows(new ErpImportFileKeyState(known, IsDeleted: false));
        GivenObject(fresh, new StoredObjectInfo(1, ShaB, Older));

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(1);
        _added.ShouldHaveSingleItem().Sha256.ShouldBe(ShaB);
        await _storage.DidNotReceive().GetObjectInfoAsync(known, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterInboxFilesAsync_OnlyKeysDirectlyInTheInboxAreConsidered()
    {
        // Arrange
        var inbox = Prefix + "inbox.xml";
        GivenListing(
            inbox,
            Prefix + "processing/claimed.xml",
            Prefix + "processed/done.xml",
            Prefix + "error/broken.xml",
            Prefix + "any/other/nested.xml");
        GivenObject(inbox, new StoredObjectInfo(1, ShaA, Older));

        // Act
        await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        await _repository.Received(1).GetKeyStatesAsync(
            Arg.Is<IReadOnlyCollection<string>>(keys => keys.SequenceEqual(new[] { inbox })),
            Arg.Any<CancellationToken>());
        _added.ShouldHaveSingleItem().StorageKey.ShouldBe(_renamedTo[inbox]);
        await _storage.Received(1).GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _storage.Received(1).TryMoveAsync(inbox, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterInboxFilesAsync_EmptyInbox_TouchesNeitherLedgerNorCommit()
    {
        // Arrange
        GivenListing(Prefix + "processed/done.xml");

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(0);
        await _repository.DidNotReceive().GetKeyStatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_FileVanishedBeforeHashing_IsSkipped()
    {
        // Arrange
        GivenListing(Prefix + "gone.xml");

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _storage.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task RegisterInboxFilesAsync_ConcurrentRegistration_DetachesThatRowAndContinues()
    {
        // Arrange
        var raced = Prefix + UploadId + "-a-raced.xml";
        var next = Prefix + UploadId + "-b-next.xml";
        GivenListing(raced, next);
        GivenObject(raced, new StoredObjectInfo(1, ShaA, Older));
        GivenObject(next, new StoredObjectInfo(2, ShaB, Older));
        _unitOfWork.CompleteAsync().Returns(
            Task.FromException(new DatabaseUpdateException("duplicate", isDuplicate: true)),
            Task.CompletedTask);

        // Act
        var registered = await _service.RegisterInboxFilesAsync(_dropPoint);

        // Assert
        registered.ShouldBe(1);
        _added.Select(f => f.StorageKey).ShouldBe(new[] { raced, next });
        _repository.Received(1).Detach(Arg.Is<ErpImportFile>(f => f.StorageKey == raced));
        _repository.DidNotReceive().Detach(Arg.Is<ErpImportFile>(f => f.StorageKey == next));
        await _unitOfWork.Received(2).CompleteAsync();
    }

    private void GivenListing(params string[] keys)
    {
        _storage.ListAsync(Prefix, Arg.Any<CancellationToken>()).Returns(keys);
    }

    private void GivenRows(params ErpImportFileKeyState[] rows)
    {
        _repository.GetKeyStatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(rows.ToList());
    }

    private void GivenObject(string key, StoredObjectInfo info)
    {
        _objects[key] = info;
    }

    private StoredObjectInfo? ObjectAt(string key)
    {
        var originalKey = _renamedTo.FirstOrDefault(pair => pair.Value == key).Key ?? key;
        return _objects.GetValueOrDefault(originalKey);
    }
}
