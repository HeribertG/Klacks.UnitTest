// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the thin payroll export handlers: CreatePayrollExportQueryHandler passes the filter and the acting
/// user of the HTTP context to IPayrollPeriodExportService (with "Unknown" when there is no user) and lets the 409
/// exceptions of the service through untouched; GetPayrollExportPreviewQueryHandler forwards the preview request.
/// </summary>
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.Exports;

[TestFixture]
public class CreatePayrollExportQueryHandlerTests
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly Until = new(2026, 1, 31);

    private IPayrollPeriodExportService _service = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private CreatePayrollExportQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _service = Substitute.For<IPayrollPeriodExportService>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tester")], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _handler = new CreatePayrollExportQueryHandler(
            _service, _httpContextAccessor, Substitute.For<ILogger<CreatePayrollExportQueryHandler>>());
    }

    private static PayrollExportFilter Filter(List<Guid>? clientIds = null) => new()
    {
        FromDate = From,
        UntilDate = Until,
        Language = "fr",
        Format = PayrollExportConstants.FormatKeyDatevLug,
        ClientIds = clientIds
    };

    [Test]
    public async Task Handle_PassesFilterAndActingUserToTheService_AndReturnsItsOutcome()
    {
        var clientIds = new List<Guid> { Guid.NewGuid() };
        var outcome = new PayrollExportOutcome { FileName = "x.csv", PersonCount = 1 };
        _service.ExportAsync(From, Until, PayrollExportConstants.FormatKeyDatevLug, "fr", clientIds, "tester", Arg.Any<CancellationToken>())
            .Returns(outcome);

        var result = await _handler.Handle(new CreatePayrollExportQuery(Filter(clientIds)), CancellationToken.None);

        result.ShouldBeSameAs(outcome);
    }

    [Test]
    public async Task Handle_UsesUnknownActor_WhenThereIsNoHttpContext()
    {
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);
        _service.ExportAsync(default, default, default!, default!, default, default!, default)
            .ReturnsForAnyArgs(new PayrollExportOutcome());

        await _handler.Handle(new CreatePayrollExportQuery(Filter()), CancellationToken.None);

        await _service.Received(1).ExportAsync(
            From, Until, PayrollExportConstants.FormatKeyDatevLug, "fr", null,
            PayrollExportConstants.UnknownActor, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_LetsTheConflictExceptionsOfTheServicePassUnchanged()
    {
        _service.ExportAsync(default, default, default!, default!, default, default!, default)
            .ReturnsForAnyArgs<Task<PayrollExportOutcome>>(_ => throw new PayrollExportNothingNewException());

        await Should.ThrowAsync<PayrollExportNothingNewException>(() =>
            _handler.Handle(new CreatePayrollExportQuery(Filter()), CancellationToken.None));
    }

    [Test]
    public async Task PreviewHandler_ForwardsTheRequest()
    {
        var scope = new List<Guid> { Guid.NewGuid() };
        var preview = new PayrollExportPreviewDto { CanExport = true };
        _service.PreviewAsync(From, Until, PayrollExportConstants.FormatKeyDatevLug, scope, Arg.Any<CancellationToken>())
            .Returns(preview);
        var handler = new GetPayrollExportPreviewQueryHandler(
            _service, Substitute.For<ILogger<GetPayrollExportPreviewQueryHandler>>());

        var result = await handler.Handle(
            new GetPayrollExportPreviewQuery(From, Until, PayrollExportConstants.FormatKeyDatevLug, scope),
            CancellationToken.None);

        result.ShouldBeSameAs(preview);
    }
}
