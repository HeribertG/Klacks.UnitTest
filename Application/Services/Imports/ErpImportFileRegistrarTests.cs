// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ErpImportFileRegistrar: a Received row with drop point, key, size, hash, channel,
/// uploader and received-at time is staged and committed; an earlier row with the same hash becomes the
/// duplicate link; blank uploaders become null and over-long values are clamped to the column length; a
/// concurrent registration (duplicate key on commit) detaches the staged row and returns null, while any
/// other database error propagates.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportFileRegistrarTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string StorageKey = "erp/orders/0123456789abcdef0123456789abcdef-orders.xml";
    private const string FileName = "orders.xml";
    private const long SizeBytes = 1234;

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private IErpImportFileRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ErpImportFileRegistrar _registrar = null!;
    private ErpDropPoint _dropPoint = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IErpImportFileRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(Now);
        _registrar = new ErpImportFileRegistrar(_repository, _unitOfWork, timeProvider, NullLogger<ErpImportFileRegistrar>.Instance);
        _dropPoint = new ErpDropPoint { Id = Guid.NewGuid(), SourceSystemId = "erp-1", BucketPrefix = "erp/orders" };
    }

    [Test]
    public async Task RegisterAsync_StagesAReceivedRowAndCommitsIt()
    {
        var file = await _registrar.RegisterAsync(Registration(ErpImportChannel.TokenPush, "ERP vendor token"));

        file.ShouldNotBeNull();
        file.Id.ShouldNotBe(Guid.Empty);
        file.DropPointId.ShouldBe(_dropPoint.Id);
        file.SourceSystemId.ShouldBe("erp-1");
        file.StorageKey.ShouldBe(StorageKey);
        file.FileName.ShouldBe(FileName);
        file.SizeBytes.ShouldBe(SizeBytes);
        file.Sha256.ShouldBe(Sha);
        file.Channel.ShouldBe(ErpImportChannel.TokenPush);
        file.UploadedBy.ShouldBe("ERP vendor token");
        file.Status.ShouldBe(ErpImportFileStatus.Received);
        file.ReceivedAt.ShouldBe(Now.UtcDateTime);
        file.ReceivedAt.Kind.ShouldBe(DateTimeKind.Utc);
        file.Attempt.ShouldBe(0);
        file.DuplicateOfFileId.ShouldBeNull();
        Received.InOrder(() =>
        {
            _repository.FindDuplicateAsync(_dropPoint.Id, Sha, null, Arg.Any<CancellationToken>());
            _repository.Add(file);
            _unitOfWork.CompleteAsync();
        });
    }

    [Test]
    public async Task RegisterAsync_EarlierRowWithTheSameHash_LinksItAsDuplicate()
    {
        var earlier = new ErpImportFile { Id = Guid.NewGuid() };
        _repository.FindDuplicateAsync(_dropPoint.Id, Sha, null, Arg.Any<CancellationToken>()).Returns(earlier);

        var file = await _registrar.RegisterAsync(Registration(ErpImportChannel.UiUpload, "admin"));

        file!.DuplicateOfFileId.ShouldBe(earlier.Id);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task RegisterAsync_BlankUploader_StoresNull(string? uploadedBy)
    {
        var file = await _registrar.RegisterAsync(Registration(ErpImportChannel.UiUpload, uploadedBy));

        file!.UploadedBy.ShouldBeNull();
    }

    [Test]
    public async Task RegisterAsync_OverLongFileNameAndUploader_AreClampedToTheColumnLength()
    {
        var registration = Registration(ErpImportChannel.UiUpload, new string('u', ErpImportFileLimits.UploadedByMaxLength + 10)) with
        {
            FileName = new string('f', ErpImportFileLimits.FileNameMaxLength + 10),
        };

        var file = await _registrar.RegisterAsync(registration);

        file!.FileName.Length.ShouldBe(ErpImportFileLimits.FileNameMaxLength);
        file.UploadedBy!.Length.ShouldBe(ErpImportFileLimits.UploadedByMaxLength);
    }

    [Test]
    public async Task RegisterAsync_CommitHitsTheUniqueKey_DetachesTheRowAndReturnsNull()
    {
        ErpImportFile? staged = null;
        _repository.Add(Arg.Do<ErpImportFile>(file => staged = file));
        _unitOfWork.CompleteAsync().ThrowsAsync(new DatabaseUpdateException("duplicate", isDuplicate: true));

        var result = await _registrar.RegisterAsync(Registration(ErpImportChannel.InboxFolder, null));

        result.ShouldBeNull();
        staged.ShouldNotBeNull();
        _repository.Received(1).Detach(staged);
    }

    [Test]
    public async Task RegisterAsync_OtherDatabaseError_Propagates_WithoutDetaching()
    {
        _unitOfWork.CompleteAsync().ThrowsAsync(new DatabaseUpdateException("fk", isForeignKeyViolation: true));

        await Should.ThrowAsync<DatabaseUpdateException>(
            () => _registrar.RegisterAsync(Registration(ErpImportChannel.UiUpload, "admin")));

        _repository.DidNotReceive().Detach(Arg.Any<ErpImportFile>());
    }

    private ErpImportFileRegistration Registration(ErpImportChannel channel, string? uploadedBy)
    {
        return new ErpImportFileRegistration(
            _dropPoint,
            StorageKey,
            FileName,
            new StoredObjectInfo(SizeBytes, Sha),
            channel,
            uploadedBy);
    }
}
