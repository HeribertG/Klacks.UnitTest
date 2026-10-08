// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for DownloadPayrollExportQueryHandler: a payroll export log with a stored artifact returns the bytes,
/// the file name, the formatter's content type and the run's metadata; an unknown id, a log without storage key, a
/// log whose key is outside the payroll prefix (order export) and an artifact missing from the storage all end in
/// KeyNotFoundException (404).
/// </summary>
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using NSubstitute;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class DownloadPayrollExportQueryHandlerTests
{
    private const string UserId = "user-42";
    private const string StorageKey = "payroll-export/20260101-20260131/abc.csv";

    private IExportLogRepository _repository = null!;
    private IPayrollArtifactStorage _storage = null!;
    private IPayrollExportFormatter _formatter = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private ILogger<DownloadPayrollExportQueryHandler> _logger = null!;
    private DownloadPayrollExportQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IExportLogRepository>();
        _storage = Substitute.For<IPayrollArtifactStorage>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _httpContextAccessor.HttpContext.Returns(HttpContextOf(UserId));
        _logger = Substitute.For<ILogger<DownloadPayrollExportQueryHandler>>();
        _formatter = Substitute.For<IPayrollExportFormatter>();
        _formatter.FormatKey.Returns(PayrollExportConstants.FormatKeyDatevLug);
        _formatter.ContentType.Returns(PayrollExportConstants.ContentTypeCsv);

        _handler = new DownloadPayrollExportQueryHandler(
            _repository, [_formatter], _storage, _httpContextAccessor, _logger);
    }

    private ExportLog GivenLog(string? storageKey = StorageKey, string format = PayrollExportConstants.FormatKeyDatevLug)
    {
        var log = new ExportLog
        {
            Id = Guid.NewGuid(),
            Format = format,
            FileName = "payroll-export_2026-01-01_2026-01-31_supplement.csv",
            PersonCount = 3,
            IsSupplementary = true,
            SkippedAbsenceCount = 2,
            StorageKey = storageKey
        };
        _repository.GetByIdAsync(log.Id, Arg.Any<CancellationToken>()).Returns(log);
        return log;
    }

    private void GivenArtifact(params byte[] content)
    {
        _storage.ReadAsync(StorageKey, Arg.Any<CancellationToken>()).Returns(content);
    }

    [Test]
    public async Task Handle_ReturnsTheStoredArtifactWithTheRunMetadata()
    {
        var log = GivenLog();
        GivenArtifact(7, 8, 9);

        var result = await _handler.Handle(new DownloadPayrollExportQuery(log.Id), CancellationToken.None);

        result.FileContent.ShouldBe(new byte[] { 7, 8, 9 });
        result.FileName.ShouldBe(log.FileName);
        result.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        result.PersonCount.ShouldBe(3);
        result.IsSupplementary.ShouldBeTrue();
        result.SkippedEntryCount.ShouldBe(2);
        result.ExportLogId.ShouldBe(log.Id);
    }

    [Test]
    public async Task Handle_FallsBackToOctetStream_WhenTheFormatIsNoLongerRegistered()
    {
        var log = GivenLog(format: "retired-format");
        GivenArtifact(1);

        var result = await _handler.Handle(new DownloadPayrollExportQuery(log.Id), CancellationToken.None);

        result.ContentType.ShouldBe(PayrollExportConstants.FallbackContentType);
    }

    [Test]
    public async Task Handle_UnknownId_IsNotFound()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _handler.Handle(new DownloadPayrollExportQuery(Guid.NewGuid()), CancellationToken.None));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("erp/orders/file.csv")]
    public async Task Handle_LogWithoutPayrollArtifact_IsNotFound_AndTheStorageIsNeverTouched(string? storageKey)
    {
        var log = GivenLog(storageKey);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _handler.Handle(new DownloadPayrollExportQuery(log.Id), CancellationToken.None));

        await _storage.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ArtifactMissingFromTheStorage_IsNotFound()
    {
        var log = GivenLog();
        _storage.ReadAsync(StorageKey, Arg.Any<CancellationToken>()).Returns((byte[]?)null);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _handler.Handle(new DownloadPayrollExportQuery(log.Id), CancellationToken.None));
    }

    [Test]
    public async Task Handle_LogsTheUserAndTheExportLogIdOnReDownload()
    {
        var log = GivenLog();
        GivenArtifact(1);

        await _handler.Handle(new DownloadPayrollExportQuery(log.Id), CancellationToken.None);

        var messages = _logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(c => c.GetArguments())
            .Where(a => (LogLevel)a[0]! == LogLevel.Information)
            .Select(a => a[2]!.ToString()!)
            .ToList();
        messages.ShouldContain(m => m.Contains(log.Id.ToString()) && m.Contains(UserId));
    }

    private static HttpContext HttpContextOf(string userId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
        return context;
    }
}