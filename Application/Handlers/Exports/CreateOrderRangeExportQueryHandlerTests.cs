// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreateOrderRangeExportQueryHandler verifying format validation, the ZIP
/// structure (one entry per exportable order plus a client period entry), entry-name
/// sanitization and collision handling, and the period-closed filtering of works and breaks.
/// </summary>
using System.IO.Compression;
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
public class CreateOrderRangeExportQueryHandlerTests
{
    private ISealedOrderIdLoader _sealedOrderIdLoader = null!;
    private IOrderExportDataLoader _orderDataLoader = null!;
    private IClientPeriodExportDataLoader _clientPeriodDataLoader = null!;
    private IExportFormatter _formatter = null!;
    private IClientPeriodExportFormatter _clientPeriodFormatter = null!;
    private IPeriodClosedEntryFilter _periodClosedEntryFilter = null!;
    private IPeriodClosedLookup _lookup = null!;
    private ICompanyInfoLoader _companyInfoLoader = null!;
    private IExportLogRepository _exportLogRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<CreateOrderRangeExportQueryHandler> _logger = null!;
    private CreateOrderRangeExportQueryHandler _handler = null!;

    private readonly Guid _closedEmployeeId = Guid.NewGuid();
    private readonly Guid _openEmployeeId = Guid.NewGuid();

    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 1, 31);
    private static readonly DateOnly ClosedDate = new(2026, 1, 10);
    private static readonly DateOnly OpenDate = new(2026, 1, 12);

    private HashSet<(Guid, DateOnly)> _closedPairs = null!;

    [SetUp]
    public void Setup()
    {
        _sealedOrderIdLoader = Substitute.For<ISealedOrderIdLoader>();
        _orderDataLoader = Substitute.For<IOrderExportDataLoader>();
        _clientPeriodDataLoader = Substitute.For<IClientPeriodExportDataLoader>();
        _formatter = Substitute.For<IExportFormatter>();
        _clientPeriodFormatter = Substitute.For<IClientPeriodExportFormatter>();
        _periodClosedEntryFilter = Substitute.For<IPeriodClosedEntryFilter>();
        _lookup = Substitute.For<IPeriodClosedLookup>();
        _companyInfoLoader = Substitute.For<ICompanyInfoLoader>();
        _exportLogRepository = Substitute.For<IExportLogRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<CreateOrderRangeExportQueryHandler>>();

        _closedPairs = [(_closedEmployeeId, ClosedDate)];

        _formatter.FormatKey.Returns(ExportConstants.FormatXml);
        _formatter.ContentType.Returns(ExportConstants.ContentTypeXml);
        _formatter.FileExtension.Returns(".xml");
        _formatter.Format(Arg.Any<OrderExportData>(), Arg.Any<ExportOptions>()).Returns([1, 2, 3]);

        _clientPeriodFormatter.ContentType.Returns(ExportConstants.ContentTypeXml);
        _clientPeriodFormatter.FileExtension.Returns(".xml");
        _clientPeriodFormatter.Format(Arg.Any<ClientPeriodExportData>(), Arg.Any<ExportOptions>()).Returns([4, 5, 6]);

        _sealedOrderIdLoader.LoadIdsForRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([Guid.NewGuid()]);

        _orderDataLoader.LoadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildOrderData());

        _clientPeriodDataLoader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(_ => BuildClientPeriodData());

        _lookup.IsClosed(Arg.Any<Guid>(), Arg.Any<DateOnly>())
            .Returns(ci => _closedPairs.Contains((ci.ArgAt<Guid>(0), ci.ArgAt<DateOnly>(1))));

        _periodClosedEntryFilter.BuildAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_lookup);

        _companyInfoLoader.LoadAsync(Arg.Any<CancellationToken>()).Returns(new CompanyInfo());

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tester")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<OrderExportResult>>>())
            .Returns(ci => ci.ArgAt<Func<Task<OrderExportResult>>>(0)());

        _handler = new CreateOrderRangeExportQueryHandler(
            _sealedOrderIdLoader,
            _orderDataLoader,
            _clientPeriodDataLoader,
            [_formatter],
            _clientPeriodFormatter,
            _periodClosedEntryFilter,
            _companyInfoLoader,
            _exportLogRepository,
            _httpContextAccessor,
            _unitOfWork,
            _logger);
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFormatUnknown()
    {
        var filter = BuildFilter();
        filter.Format = "unknown";

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreateOrderRangeExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFromDateAfterUntilDate()
    {
        var filter = BuildFilter();
        filter.FromDate = new DateOnly(2026, 2, 1);
        filter.UntilDate = new DateOnly(2026, 1, 1);

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreateOrderRangeExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_Zip_ContainsSanitizedOrderEntriesAndClientPeriodEntry_OmittingOrdersWithoutClosedWorks()
    {
        var result = await _handler.Handle(new CreateOrderRangeExportQuery(BuildFilter()), CancellationToken.None);

        result.ContentType.ShouldBe(ExportConstants.ContentTypeZip);
        result.FileName.ShouldBe("erp-order-export_2026-01-01_2026-01-31.zip");

        var entryNames = ReadZipEntryNames(result.FileContent);

        entryNames.ShouldContain("order_PO-4711-A.xml");
        entryNames.ShouldContain("order_PO-4711-A_2.xml");
        entryNames.ShouldContain("client-period-export_2026-01-01_2026-01-31.xml");
        entryNames.Count.ShouldBe(3);
    }

    [Test]
    public async Task Handle_PassesOnlyPeriodClosedWorksAndBreaks_ToTheOrderFormatter()
    {
        var formattedOrders = new List<OrderExportData>();
        _formatter.Format(Arg.Do<OrderExportData>(formattedOrders.Add), Arg.Any<ExportOptions>()).Returns([1, 2, 3]);

        await _handler.Handle(new CreateOrderRangeExportQuery(BuildFilter()), CancellationToken.None);

        formattedOrders.Count.ShouldBe(2);

        foreach (var data in formattedOrders)
        {
            data.Orders.Count.ShouldBe(1);
        }

        var firstOrder = formattedOrders[0].Orders[0];
        firstOrder.WorkEntries.Count.ShouldBe(1);
        firstOrder.WorkEntries[0].EmployeeId.ShouldBe(_closedEmployeeId);
        firstOrder.WorkEntries[0].WorkDate.ShouldBe(ClosedDate);
        firstOrder.WorkEntries[0].Breaks.Count.ShouldBe(1);
        firstOrder.WorkEntries[0].Breaks[0].BreakDate.ShouldBe(ClosedDate);
    }

    [Test]
    public async Task Handle_PassesOnlyPeriodClosedClients_ToTheClientPeriodFormatter()
    {
        ClientPeriodExportData? formattedData = null;
        _clientPeriodFormatter.Format(Arg.Do<ClientPeriodExportData>(d => formattedData = d), Arg.Any<ExportOptions>()).Returns([4, 5, 6]);

        await _handler.Handle(new CreateOrderRangeExportQuery(BuildFilter()), CancellationToken.None);

        formattedData.ShouldNotBeNull();
        formattedData.Clients.Count.ShouldBe(1);
        formattedData.Clients[0].ClientId.ShouldBe(_closedEmployeeId);
        formattedData.Clients[0].WorkEntries.Count.ShouldBe(1);
        formattedData.Clients[0].WorkEntries[0].WorkDate.ShouldBe(ClosedDate);
    }

    [Test]
    public async Task Handle_PersistsExportLogEntry_WithRangePrefixedFormat()
    {
        await _handler.Handle(new CreateOrderRangeExportQuery(BuildFilter()), CancellationToken.None);

        await _exportLogRepository.Received(1).AddAsync(
            Arg.Is<ExportLog>(e =>
                e.Format == ExportConstants.RangeFormatPrefix + ExportConstants.FormatXml &&
                e.StartDate == FromDate &&
                e.EndDate == UntilDate &&
                e.RecordCount == 2 &&
                e.ExportedBy == "tester"),
            Arg.Any<CancellationToken>());
    }

    private static OrderRangeExportFilter BuildFilter()
    {
        return new OrderRangeExportFilter
        {
            FromDate = FromDate,
            UntilDate = UntilDate,
            Format = ExportConstants.FormatXml,
            Language = "de",
            CurrencyCode = "EUR",
        };
    }

    private OrderExportData BuildOrderData()
    {
        return new OrderExportData
        {
            StartDate = FromDate,
            EndDate = UntilDate,
            Orders =
            [
                BuildOrderGroup("PO 4711/A", withClosedWork: true),
                BuildOrderGroup("PO 4711/A", withClosedWork: true),
                BuildOrderGroup(null, withClosedWork: false),
            ],
        };
    }

    private OrderGroup BuildOrderGroup(string? externalOrderReference, bool withClosedWork)
    {
        var workEntries = new List<WorkExportEntry>
        {
            new()
            {
                WorkId = Guid.NewGuid(),
                EmployeeId = _openEmployeeId,
                EmployeeName = "Open, Employee",
                WorkDate = OpenDate,
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(17, 0),
                WorkTime = 8m,
            },
        };

        if (withClosedWork)
        {
            workEntries.Add(new WorkExportEntry
            {
                WorkId = Guid.NewGuid(),
                EmployeeId = _closedEmployeeId,
                EmployeeName = "Closed, Employee",
                WorkDate = ClosedDate,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(16, 0),
                WorkTime = 8m,
                Breaks =
                [
                    new BreakExportEntry
                    {
                        AbsenceName = "Lunch",
                        BreakDate = ClosedDate,
                        StartTime = new TimeOnly(12, 0),
                        EndTime = new TimeOnly(13, 0),
                        BreakTime = 1m,
                    },
                    new BreakExportEntry
                    {
                        AbsenceName = "Lunch",
                        BreakDate = OpenDate,
                        StartTime = new TimeOnly(12, 0),
                        EndTime = new TimeOnly(13, 0),
                        BreakTime = 1m,
                    },
                ],
            });
        }

        return new OrderGroup
        {
            OrderShiftId = Guid.NewGuid(),
            OrderName = "Order",
            OrderAbbreviation = "ORD",
            ExternalOrderReference = externalOrderReference,
            WorkEntries = workEntries,
        };
    }

    private ClientPeriodExportData BuildClientPeriodData()
    {
        return new ClientPeriodExportData
        {
            StartDate = FromDate,
            EndDate = UntilDate,
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = _closedEmployeeId,
                    ClientName = "Closed, Employee",
                    WorkEntries =
                    [
                        new ClientWorkExportEntry { WorkId = Guid.NewGuid(), WorkDate = ClosedDate, WorkTime = 8m },
                        new ClientWorkExportEntry { WorkId = Guid.NewGuid(), WorkDate = OpenDate, WorkTime = 8m },
                    ],
                },
                new ClientPeriodGroup
                {
                    ClientId = _openEmployeeId,
                    ClientName = "Open, Employee",
                    WorkEntries =
                    [
                        new ClientWorkExportEntry { WorkId = Guid.NewGuid(), WorkDate = OpenDate, WorkTime = 8m },
                    ],
                },
            ],
        };
    }

    private static List<string> ReadZipEntryNames(byte[] zipContent)
    {
        using var stream = new MemoryStream(zipContent);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.Select(e => e.FullName).ToList();
    }
}
