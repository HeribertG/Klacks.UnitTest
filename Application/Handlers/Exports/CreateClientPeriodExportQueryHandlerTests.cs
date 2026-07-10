// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests verifying that CreateClientPeriodExportQueryHandler persists an ExportLog entry on
/// successful export, and rejects an inverted date range.
/// @param filter - Contains the date range and localization settings used to drive the export
/// </summary>
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class CreateClientPeriodExportQueryHandlerTests
{
    private IClientPeriodExportDataLoader _dataLoader = null!;
    private IClientPeriodExportFormatter _formatter = null!;
    private IExportLogRepository _exportLogRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<CreateClientPeriodExportQueryHandler> _logger = null!;
    private CreateClientPeriodExportQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _dataLoader = Substitute.For<IClientPeriodExportDataLoader>();
        _formatter = Substitute.For<IClientPeriodExportFormatter>();
        _exportLogRepository = Substitute.For<IExportLogRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<CreateClientPeriodExportQueryHandler>>();

        _formatter.FormatKey.Returns(ExportConstants.FormatXml);
        _formatter.ContentType.Returns("application/xml");
        _formatter.FileExtension.Returns(".xml");
        _formatter.Format(Arg.Any<ClientPeriodExportData>(), Arg.Any<ExportOptions>()).Returns(new byte[] { 1, 2, 3 });

        _dataLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new ClientPeriodExportData
            {
                Clients = [],
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 1, 31)
            });

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tester")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<OrderExportResult>>>())
            .Returns(ci => ci.ArgAt<Func<Task<OrderExportResult>>>(0)());

        _handler = new CreateClientPeriodExportQueryHandler(
            _dataLoader,
            [_formatter],
            _exportLogRepository,
            _httpContextAccessor,
            _unitOfWork,
            _logger);
    }

    [Test]
    public async Task Handle_PersistsExportLogEntry_OnSuccess()
    {
        var filter = new ClientPeriodExportFilter
        {
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = new DateOnly(2026, 1, 31),
            Language = "de",
            CurrencyCode = "EUR"
        };

        var result = await _handler.Handle(new CreateClientPeriodExportQuery(filter), CancellationToken.None);

        result.FileContent.ShouldBe(new byte[] { 1, 2, 3 });

        await _exportLogRepository.Received(1).AddAsync(
            Arg.Is<ExportLog>(e =>
                e.Format == $"clientperiod-{ExportConstants.FormatXml}" &&
                e.FileSize == 3 &&
                e.ExportedBy == "tester"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFromDateAfterUntilDate()
    {
        var filter = new ClientPeriodExportFilter
        {
            FromDate = new DateOnly(2026, 2, 1),
            UntilDate = new DateOnly(2026, 1, 1),
        };

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreateClientPeriodExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFormatUnknown()
    {
        var filter = new ClientPeriodExportFilter
        {
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = new DateOnly(2026, 1, 31),
            Format = "does-not-exist"
        };

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreateClientPeriodExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_SelectsFormatterMatchingTheRequestedFormat()
    {
        var csvFormatter = Substitute.For<IClientPeriodExportFormatter>();
        csvFormatter.FormatKey.Returns(ExportConstants.FormatCsv);
        csvFormatter.ContentType.Returns(ExportConstants.ContentTypeCsv);
        csvFormatter.FileExtension.Returns(".csv");
        csvFormatter.Format(Arg.Any<ClientPeriodExportData>(), Arg.Any<ExportOptions>()).Returns(new byte[] { 9 });

        var handler = new CreateClientPeriodExportQueryHandler(
            _dataLoader,
            [_formatter, csvFormatter],
            _exportLogRepository,
            _httpContextAccessor,
            _unitOfWork,
            _logger);

        var filter = new ClientPeriodExportFilter
        {
            FromDate = new DateOnly(2026, 1, 1),
            UntilDate = new DateOnly(2026, 1, 31),
            Format = ExportConstants.FormatCsv
        };

        var result = await handler.Handle(new CreateClientPeriodExportQuery(filter), CancellationToken.None);

        result.FileContent.ShouldBe(new byte[] { 9 });
        result.ContentType.ShouldBe(ExportConstants.ContentTypeCsv);
    }
}
