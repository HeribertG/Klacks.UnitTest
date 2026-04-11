// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests verifying that CreateOrderExportQueryHandler persists an ExportLog entry on successful export.
/// @param filter - Contains date range, format key and localization settings used to drive the export
/// </summary>

using FluentAssertions;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class CreateOrderExportQueryHandlerExportLogTests
{
    private IOrderExportDataLoader _dataLoader = null!;
    private IExportFormatter _formatter = null!;
    private ISettingsReader _settingsReader = null!;
    private IExportLogRepository _exportLogRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<CreateOrderExportQueryHandler> _logger = null!;
    private CreateOrderExportQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _dataLoader = Substitute.For<IOrderExportDataLoader>();
        _formatter = Substitute.For<IExportFormatter>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _exportLogRepository = Substitute.For<IExportLogRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<CreateOrderExportQueryHandler>>();

        _formatter.FormatKey.Returns("csv");
        _formatter.ContentType.Returns("text/csv");
        _formatter.FileExtension.Returns(".csv");
        _formatter.Format(Arg.Any<OrderExportData>(), Arg.Any<ExportOptions>()).Returns(new byte[] { 1, 2, 3 });

        _dataLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new OrderExportData
            {
                Orders = [],
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 1, 31)
            });

        _settingsReader.GetSetting(Arg.Any<string>()).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tester")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<OrderExportResult>>>())
            .Returns(ci => ci.ArgAt<Func<Task<OrderExportResult>>>(0)());

        _handler = new CreateOrderExportQueryHandler(
            _dataLoader,
            [_formatter],
            _settingsReader,
            _exportLogRepository,
            _httpContextAccessor,
            _unitOfWork,
            _logger);
    }

    [Test]
    public async Task Handle_PersistsExportLogEntry_OnSuccess()
    {
        var filter = new OrderExportFilter
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Format = "csv",
            Language = "de",
            CurrencyCode = "EUR"
        };

        var result = await _handler.Handle(new CreateOrderExportQuery(filter), CancellationToken.None);

        result.FileContent.Should().Equal(new byte[] { 1, 2, 3 });

        await _exportLogRepository.Received(1).AddAsync(
            Arg.Is<ExportLog>(e =>
                e.Format == "csv" &&
                e.FileSize == 3 &&
                e.ExportedBy == "tester"),
            Arg.Any<CancellationToken>());
    }
}
