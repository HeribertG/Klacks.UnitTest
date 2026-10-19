// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the id-based retry of an erp_import_file row: stale imports are reaped first, only Failed
/// and Interrupted rows can be retried, the strict hash re-check fails a changed or missing file without
/// re-queueing it, and an intact file is reset to Received with its open exceptions resolved inside one
/// transaction before an import run is triggered.
/// </summary>

using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class RetryErpOrderFileCommandHandlerTests
{
    private const string StorageKey = "erp/orders/0123456789abcdef0123456789abcdef-orders.xml";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    private IErpImportFileRepository _fileRepository = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private RetryErpOrderFileCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mediator = Substitute.For<IMediator>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _fileRepository.TryResetForRetryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _fileRepository.TryMarkFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _objectStorageService.GetObjectInfoAsync(StorageKey, Arg.Any<CancellationToken>()).Returns(new StoredObjectInfo(42, Sha));

        _handler = new RetryErpOrderFileCommandHandler(
            _fileRepository, _exceptionRepository, _objectStorageService, _unitOfWork, _mediator, new SettableTimeProvider(Now));
    }

    [TestCase(ErpImportFileStatus.Failed)]
    [TestCase(ErpImportFileStatus.Interrupted)]
    public async Task Handle_IntactRetryableFile_ResetsItResolvesItsExceptionsAndTriggersARun(ErpImportFileStatus status)
    {
        // Arrange
        var file = GivenFile(status);

        // Act
        var result = await _handler.Handle(new RetryErpOrderFileCommand(file.Id), CancellationToken.None);

        // Assert
        result.Id.ShouldBe(file.Id);
        result.Status.ShouldBe(ErpImportFileStatus.Received);
        result.FailureReason.ShouldBeNull();
        Received.InOrder(() =>
        {
            _fileRepository.ReapStaleAsync(Now - ErpImportFileTiming.StaleAfter, Arg.Any<CancellationToken>());
            _fileRepository.GetByIdAsync(file.Id, Arg.Any<CancellationToken>());
            _fileRepository.TryResetForRetryAsync(file.Id, Arg.Any<CancellationToken>());
            _exceptionRepository.ResolveOpenForImportFileAsync(file.Id, Now, Arg.Any<CancellationToken>());
            _mediator.Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
        });
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
    }

    [Test]
    public async Task Handle_FileChangedAfterReceipt_MarksItFailedWithoutRequeueing()
    {
        // Arrange
        var file = GivenFile(ErpImportFileStatus.Interrupted);
        _objectStorageService.GetObjectInfoAsync(StorageKey, Arg.Any<CancellationToken>()).Returns(new StoredObjectInfo(42, OtherSha));

        // Act
        var result = await _handler.Handle(new RetryErpOrderFileCommand(file.Id), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ErpImportFileStatus.Failed);
        result.FailureReason.ShouldBe(ErpImportFileFailureReasons.FileChangedAfterReceipt);
        await _fileRepository.Received(1).TryMarkFailedAsync(file.Id, ErpImportFileFailureReasons.FileChangedAfterReceipt, Now, Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().TryResetForRetryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _exceptionRepository.DidNotReceive().ResolveOpenForImportFileAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_StoredFileMissing_MarksItFailedWithItsOwnReason()
    {
        // Arrange
        var file = GivenFile(ErpImportFileStatus.Failed);
        _objectStorageService.GetObjectInfoAsync(StorageKey, Arg.Any<CancellationToken>()).Returns((StoredObjectInfo?)null);

        // Act
        var result = await _handler.Handle(new RetryErpOrderFileCommand(file.Id), CancellationToken.None);

        // Assert
        result.Status.ShouldBe(ErpImportFileStatus.Failed);
        result.FailureReason.ShouldBe(ErpImportFileFailureReasons.StoredFileMissing);
        await _fileRepository.DidNotReceive().TryResetForRetryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [TestCase(ErpImportFileStatus.Received)]
    [TestCase(ErpImportFileStatus.Importing)]
    [TestCase(ErpImportFileStatus.Completed)]
    [TestCase(ErpImportFileStatus.CompletedWithWarnings)]
    public async Task Handle_FileNotFailedOrInterrupted_IsRejected(ErpImportFileStatus status)
    {
        // Arrange
        var file = GivenFile(status);

        // Act + Assert
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new RetryErpOrderFileCommand(file.Id), CancellationToken.None));
        await _fileRepository.DidNotReceive().TryResetForRetryAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnknownId_IsNotFound()
    {
        // Act + Assert
        await Should.ThrowAsync<NotFoundException>(
            () => _handler.Handle(new RetryErpOrderFileCommand(Guid.NewGuid()), CancellationToken.None));
    }

    [Test]
    public async Task Handle_RowChangedConcurrently_IsAConflictAndResolvesNothing()
    {
        // Arrange
        var file = GivenFile(ErpImportFileStatus.Failed);
        _fileRepository.TryResetForRetryAsync(file.Id, Arg.Any<CancellationToken>()).Returns(false);

        // Act + Assert
        await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(new RetryErpOrderFileCommand(file.Id), CancellationToken.None));
        await _exceptionRepository.DidNotReceive().ResolveOpenForImportFileAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<TriggerErpImportRunCommand>(), Arg.Any<CancellationToken>());
    }

    private ErpImportFile GivenFile(ErpImportFileStatus status)
    {
        var file = new ErpImportFile
        {
            Id = Guid.NewGuid(),
            StorageKey = StorageKey,
            FileName = "orders.xml",
            Sha256 = Sha,
            Status = status
        };
        _fileRepository.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        return file;
    }
}
