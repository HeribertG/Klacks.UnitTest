// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the id-based delete of an erp_import_file row: stale imports are reaped first, only Failed
/// and Interrupted rows can be deleted, the row is soft-deleted (together with the resolution of the file's
/// open exceptions, in one transaction) BEFORE the stored object is deleted, so a delete that loses against a
/// concurrent retry neither resolves anything nor removes the re-queued file's object, and a failing object
/// deletion afterwards is only logged -- the row stays deleted.
/// </summary>

using Klacks.Api.Application.Commands.Imports;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Application.Handlers.Imports;

[TestFixture]
public class DeleteErpOrderFileCommandHandlerTests
{
    private const string StorageKey = "erp/orders/0123456789abcdef0123456789abcdef-orders.xml";

    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    private IErpImportFileRepository _fileRepository = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteErpOrderFileCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _fileRepository.TrySoftDeleteAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

        _handler = new DeleteErpOrderFileCommandHandler(
            _fileRepository, _exceptionRepository, _objectStorageService, _unitOfWork, new SettableTimeProvider(Now),
            NullLogger<DeleteErpOrderFileCommandHandler>.Instance);
    }

    [TestCase(ErpImportFileStatus.Failed)]
    [TestCase(ErpImportFileStatus.Interrupted)]
    public async Task Handle_FailedOrInterruptedFile_SoftDeletesTheRowThenDeletesTheObject(ErpImportFileStatus status)
    {
        // Arrange
        var file = GivenFile(status);

        // Act
        await _handler.Handle(new DeleteErpOrderFileCommand(file.Id), CancellationToken.None);

        // Assert
        Received.InOrder(() =>
        {
            _fileRepository.ReapStaleAsync(Now - ErpImportFileTiming.StaleAfter, Arg.Any<CancellationToken>());
            _fileRepository.GetByIdAsync(file.Id, Arg.Any<CancellationToken>());
            _fileRepository.TrySoftDeleteAsync(file.Id, Now, Arg.Any<CancellationToken>());
            _exceptionRepository.ResolveOpenForImportFileAsync(file.Id, Now, Arg.Any<CancellationToken>());
            _objectStorageService.DeleteAsync(StorageKey, Arg.Any<CancellationToken>());
        });
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
    }

    [TestCase(ErpImportFileStatus.Received)]
    [TestCase(ErpImportFileStatus.Importing)]
    [TestCase(ErpImportFileStatus.Completed)]
    [TestCase(ErpImportFileStatus.CompletedWithWarnings)]
    public async Task Handle_FileNotFailedOrInterrupted_IsRejectedAndKeepsItsObject(ErpImportFileStatus status)
    {
        // Arrange
        var file = GivenFile(status);

        // Act + Assert
        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new DeleteErpOrderFileCommand(file.Id), CancellationToken.None));
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().TrySoftDeleteAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_UnknownId_IsNotFound()
    {
        // Act + Assert
        await Should.ThrowAsync<NotFoundException>(
            () => _handler.Handle(new DeleteErpOrderFileCommand(Guid.NewGuid()), CancellationToken.None));
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_RetriedConcurrently_IsAConflictThatKeepsTheObjectAndResolvesNothing()
    {
        // Arrange
        var file = GivenFile(ErpImportFileStatus.Failed);
        _fileRepository.TrySoftDeleteAsync(file.Id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(false);

        // Act + Assert
        await Should.ThrowAsync<ConflictException>(
            () => _handler.Handle(new DeleteErpOrderFileCommand(file.Id), CancellationToken.None));
        await _exceptionRepository.DidNotReceive().ResolveOpenForImportFileAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ObjectDeletionFailingAfterTheSoftDelete_IsLoggedAndTheRowStaysDeleted()
    {
        // Arrange
        var file = GivenFile(ErpImportFileStatus.Failed);
        _objectStorageService.DeleteAsync(StorageKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("file locked"));

        // Act
        await _handler.Handle(new DeleteErpOrderFileCommand(file.Id), CancellationToken.None);

        // Assert
        await _fileRepository.Received(1).TrySoftDeleteAsync(file.Id, Now, Arg.Any<CancellationToken>());
        await _exceptionRepository.Received(1).ResolveOpenForImportFileAsync(file.Id, Now, Arg.Any<CancellationToken>());
        await _objectStorageService.Received(1).DeleteAsync(StorageKey, Arg.Any<CancellationToken>());
    }

    private ErpImportFile GivenFile(ErpImportFileStatus status)
    {
        var file = new ErpImportFile { Id = Guid.NewGuid(), StorageKey = StorageKey, FileName = "orders.xml", Status = status };
        _fileRepository.GetByIdAsync(file.Id, Arg.Any<CancellationToken>()).Returns(file);
        return file;
    }
}
