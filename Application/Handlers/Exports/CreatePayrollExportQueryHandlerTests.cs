// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CreatePayrollExportQueryHandler: it persists an ExportLog row on success,
/// resolves the formatter by the requested format key, and rejects a missing group, an inverted
/// date range, an unknown/disabled format and a period with no closed payroll data.
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
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class CreatePayrollExportQueryHandlerTests
{
    private static readonly Guid GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IMediator _mediator = null!;
    private IPayrollExportFormatter _formatter = null!;
    private IPayrollExportConfigRepository _configRepository = null!;
    private IExportFormatPolicy _exportFormatPolicy = null!;
    private IExportLogRepository _exportLogRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<CreatePayrollExportQueryHandler> _logger = null!;
    private CreatePayrollExportQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _formatter = Substitute.For<IPayrollExportFormatter>();
        _configRepository = Substitute.For<IPayrollExportConfigRepository>();
        _exportFormatPolicy = Substitute.For<IExportFormatPolicy>();
        _exportLogRepository = Substitute.For<IExportLogRepository>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<CreatePayrollExportQueryHandler>>();

        _formatter.FormatKey.Returns(PayrollExportConstants.FormatKeyDatevLug);
        _formatter.ContentType.Returns(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.Returns(PayrollExportConstants.FileExtensionCsv);
        _formatter.Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>())
            .Returns(new PayrollExportResult { Content = [1, 2, 3], RecordCount = 2 });

        _configRepository.GetByGroupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new PayrollExportGroupConfig { GroupId = GroupId, TargetSystem = PayrollExportConstants.FormatKeyDatevLug });

        _exportFormatPolicy.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _mediator.Send(Arg.Any<GetPayrollPeriodDataQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PayrollExportData
            {
                GroupId = GroupId,
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 1, 31),
                Employees = [new PayrollEmployee()]
            });

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tester")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<OrderExportResult>>>())
            .Returns(ci => ci.ArgAt<Func<Task<OrderExportResult>>>(0)());

        _handler = CreateHandler(_formatter);
    }

    private CreatePayrollExportQueryHandler CreateHandler(params IPayrollExportFormatter[] formatters) =>
        new(
            _mediator,
            formatters,
            _configRepository,
            _exportFormatPolicy,
            _exportLogRepository,
            _httpContextAccessor,
            _unitOfWork,
            _logger);

    private static PayrollExportFilter ValidFilter() => new()
    {
        GroupId = GroupId,
        FromDate = new DateOnly(2026, 1, 1),
        UntilDate = new DateOnly(2026, 1, 31),
        Language = "de",
        Format = PayrollExportConstants.FormatKeyDatevLug
    };

    [Test]
    public async Task Handle_PersistsExportLogEntry_OnSuccess()
    {
        var result = await _handler.Handle(new CreatePayrollExportQuery(ValidFilter()), CancellationToken.None);

        result.FileContent.ShouldBe(new byte[] { 1, 2, 3 });
        result.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);

        await _exportLogRepository.Received(1).AddAsync(
            Arg.Is<ExportLog>(e =>
                e.Format == PayrollExportConstants.FormatKeyDatevLug &&
                e.GroupId == GroupId &&
                e.FileSize == 3 &&
                e.RecordCount == 2 &&
                e.ExportedBy == "tester"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenGroupMissing()
    {
        var filter = ValidFilter();
        filter.GroupId = Guid.Empty;

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreatePayrollExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFromDateAfterUntilDate()
    {
        var filter = ValidFilter();
        filter.FromDate = new DateOnly(2026, 2, 1);
        filter.UntilDate = new DateOnly(2026, 1, 1);

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreatePayrollExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFormatUnknown()
    {
        var filter = ValidFilter();
        filter.Format = "does-not-exist";

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreatePayrollExportQuery(filter), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenFormatDisabled()
    {
        _exportFormatPolicy.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreatePayrollExportQuery(ValidFilter()), CancellationToken.None));
    }

    [Test]
    public async Task Handle_ThrowsInvalidRequestException_WhenPeriodHasNoClosedData()
    {
        _mediator.Send(Arg.Any<GetPayrollPeriodDataQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PayrollExportData
            {
                GroupId = GroupId,
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 1, 31),
                Employees = []
            });

        await Should.ThrowAsync<InvalidRequestException>(async () =>
            await _handler.Handle(new CreatePayrollExportQuery(ValidFilter()), CancellationToken.None));
    }

    [Test]
    public async Task Handle_SelectsFormatterMatchingTheRequestedFormat()
    {
        var paxml = Substitute.For<IPayrollExportFormatter>();
        paxml.FormatKey.Returns(PayrollExportConstants.FormatKeyPaxmlSe);
        paxml.ContentType.Returns(PayrollExportConstants.ContentTypeXml);
        paxml.FileExtension.Returns(PayrollExportConstants.FileExtensionXml);
        paxml.Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>())
            .Returns(new PayrollExportResult { Content = [9], RecordCount = 1 });

        var handler = CreateHandler(_formatter, paxml);

        var filter = ValidFilter();
        filter.Format = PayrollExportConstants.FormatKeyPaxmlSe;

        var result = await handler.Handle(new CreatePayrollExportQuery(filter), CancellationToken.None);

        result.FileContent.ShouldBe(new byte[] { 9 });
        result.ContentType.ShouldBe(PayrollExportConstants.ContentTypeXml);
    }
}
