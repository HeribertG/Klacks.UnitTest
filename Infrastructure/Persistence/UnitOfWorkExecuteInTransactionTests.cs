// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that UnitOfWork.ExecuteInTransactionAsync reports the failure of its closing save like CompleteAsync does: a
/// unique violation becomes the Domain DatabaseUpdateException (IsDuplicate), a concurrency failure the Domain
/// ConcurrencyException, both with the EF Core exception as inner exception. An exception of the operation itself that
/// is not an EF Core write failure passes through unchanged. The translation happens outside the execution strategy, so
/// the retrying strategy of production still classifies the raw EF Core exception. Runs over EF InMemory with a save that
/// fails like PostgreSQL.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Persistence;

[TestFixture]
public class UnitOfWorkExecuteInTransactionTests
{
    private const int OperationResult = 42;

    private SaveChangesFailingDataBaseContext _context = null!;
    private UnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .ReplaceService<IExecutionStrategyFactory, RecordingExecutionStrategyFactory>()
            .Options;
        RecordingExecutionStrategyFactory.Seen.Clear();
        _context = new SaveChangesFailingDataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public async Task ExecuteInTransactionAsync_ClosingSaveSucceeds_ReturnsTheOperationResult()
    {
        var result = await _unitOfWork.ExecuteInTransactionAsync(() => Task.FromResult(OperationResult));

        result.ShouldBe(OperationResult);
    }

    [Test]
    public async Task ExecuteInTransactionAsync_ClosingSaveHitsAUniqueViolation_ThrowsDatabaseUpdateExceptionMarkedDuplicate()
    {
        _context.FailNextSave = SaveChangesFailingDataBaseContext.UniqueViolation;

        var thrown = await Should.ThrowAsync<DatabaseUpdateException>(
            () => _unitOfWork.ExecuteInTransactionAsync(() => Task.FromResult(OperationResult)));

        thrown.IsDuplicate.ShouldBeTrue();
        thrown.InnerException.ShouldBeOfType<DbUpdateException>();
    }

    [Test]
    public async Task ExecuteInTransactionAsync_ClosingSaveHitsAConcurrencyFailure_ThrowsConcurrencyException()
    {
        _context.FailNextSave = () => new DbUpdateConcurrencyException("row moved");

        var thrown = await Should.ThrowAsync<ConcurrencyException>(
            () => _unitOfWork.ExecuteInTransactionAsync(() => Task.FromResult(OperationResult)));

        thrown.InnerException.ShouldBeOfType<DbUpdateConcurrencyException>();
    }

    [Test]
    public async Task ExecuteInTransactionAsync_ClosingSaveFails_TheExecutionStrategyClassifiesTheRawEfCoreException()
    {
        _context.FailNextSave = SaveChangesFailingDataBaseContext.UniqueViolation;

        await Should.ThrowAsync<DatabaseUpdateException>(
            () => _unitOfWork.ExecuteInTransactionAsync(() => Task.FromResult(OperationResult)));

        RecordingExecutionStrategyFactory.Seen.ShouldNotBeEmpty();
        RecordingExecutionStrategyFactory.Seen.OfType<DatabaseUpdateException>().ShouldBeEmpty();
        RecordingExecutionStrategyFactory.Seen.OfType<ConcurrencyException>().ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteInTransactionAsync_OperationThrowsAnUnrelatedException_PassesItThroughUnchanged()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _unitOfWork.ExecuteInTransactionAsync<int>(() => throw new InvalidOperationException("boom")));
    }
}
