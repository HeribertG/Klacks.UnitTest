// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollExportController: the class is restricted to the Admin role with the JWT scheme pinned
/// (the rights model relies on this attribute, see ControllerAuthorizeSchemeGuardTests for the scheme rule); the
/// export answers with the file plus the skipped, persons and supplementary headers (supplementary and mapping-invalid
/// only when true); preview forwards the query and treats an empty client list as "everybody"; download returns the
/// stored file and lets the not-found of the handler pass to the middleware.
/// </summary>
using System.Reflection;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Exports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class PayrollExportControllerTests
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly Until = new(2026, 1, 31);

    private IMediator _mediator = null!;
    private PayrollExportController _controller = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _controller = new PayrollExportController(_mediator)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static PayrollExportOutcome Outcome(bool supplementary = false, bool mappingInvalid = false) => new()
    {
        FileContent = [1, 2],
        FileName = "payroll-export.csv",
        ContentType = "text/csv",
        SkippedEntryCount = 4,
        AbsenceMappingInvalid = mappingInvalid,
        PersonCount = 3,
        IsSupplementary = supplementary,
        ExportLogId = Guid.NewGuid()
    };

    [Test]
    public void Controller_IsAdminOnly_WithTheJwtSchemePinned()
    {
        var authorize = typeof(PayrollExportController).GetCustomAttributes<AuthorizeAttribute>(inherit: false).ShouldHaveSingleItem();

        authorize.Roles.ShouldBe(Roles.Admin);
        authorize.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
    }

    [Test]
    public void EveryAction_HasAnHttpVerbAndNoAllowAnonymous()
    {
        var actions = typeof(PayrollExportController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        actions.Length.ShouldBe(3);
        foreach (var action in actions)
        {
            action.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().ShouldNotBeEmpty(action.Name);
            action.GetCustomAttributes<AllowAnonymousAttribute>().ShouldBeEmpty(action.Name);
        }
    }

    [Test]
    public async Task Export_ReturnsTheFile_WithSkippedAndPersonHeaders_AndNoOptionalFlags()
    {
        _mediator.Send(Arg.Any<CreatePayrollExportQuery>(), Arg.Any<CancellationToken>()).Returns(Outcome());

        var result = await _controller.Export(new PayrollExportFilter { FromDate = From, UntilDate = Until }, CancellationToken.None);

        var file = result.ShouldBeOfType<FileContentResult>();
        file.FileContents.ShouldBe(new byte[] { 1, 2 });
        file.FileDownloadName.ShouldBe("payroll-export.csv");
        file.ContentType.ShouldBe("text/csv");
        var headers = _controller.Response.Headers;
        headers[ExportResponseHeaders.SkippedEntries].ToString().ShouldBe("4");
        headers[ExportResponseHeaders.Persons].ToString().ShouldBe("3");
        headers.ContainsKey(ExportResponseHeaders.Supplementary).ShouldBeFalse();
        headers.ContainsKey(ExportResponseHeaders.AbsenceMappingInvalid).ShouldBeFalse();
    }

    [Test]
    public async Task Export_SetsTheSupplementaryAndMappingInvalidHeaders_WhenTrue()
    {
        _mediator.Send(Arg.Any<CreatePayrollExportQuery>(), Arg.Any<CancellationToken>())
            .Returns(Outcome(supplementary: true, mappingInvalid: true));

        await _controller.Export(new PayrollExportFilter(), CancellationToken.None);

        var headers = _controller.Response.Headers;
        headers[ExportResponseHeaders.Supplementary].ToString().ShouldBe(bool.TrueString);
        headers[ExportResponseHeaders.AbsenceMappingInvalid].ToString().ShouldBe(bool.TrueString);
    }

    [Test]
    public async Task Preview_ForwardsTheQuery_AndTreatsAnEmptyClientListAsEverybody()
    {
        var preview = new PayrollExportPreviewDto { CanExport = true };
        _mediator.Send(Arg.Any<GetPayrollExportPreviewQuery>(), Arg.Any<CancellationToken>()).Returns(preview);

        var result = await _controller.Preview(From, Until, "datev-lug-bewegungsdaten", [], CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeSameAs(preview);
        await _mediator.Received(1).Send(
            Arg.Is<GetPayrollExportPreviewQuery>(q => q.FromDate == From && q.UntilDate == Until
                && q.Format == "datev-lug-bewegungsdaten" && q.ClientIds == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_PassesAClientSelectionThrough()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetPayrollExportPreviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(new PayrollExportPreviewDto());

        await _controller.Preview(From, Until, "paxml-se", [id], CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<GetPayrollExportPreviewQuery>(q => q.ClientIds != null && q.ClientIds.Count == 1 && q.ClientIds.Contains(id)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Download_ReturnsTheStoredFile()
    {
        var outcome = Outcome();
        _mediator.Send(Arg.Is<DownloadPayrollExportQuery>(q => q.ExportLogId == outcome.ExportLogId), Arg.Any<CancellationToken>())
            .Returns(outcome);

        var result = await _controller.Download(outcome.ExportLogId, CancellationToken.None);

        result.ShouldBeOfType<FileContentResult>().FileDownloadName.ShouldBe("payroll-export.csv");
    }

    [Test]
    public async Task Download_LetsTheNotFoundOfTheHandlerPassToTheMiddleware()
    {
        _mediator.Send(Arg.Any<DownloadPayrollExportQuery>(), Arg.Any<CancellationToken>())
            .Returns<Task<PayrollExportOutcome>>(_ => throw new KeyNotFoundException("Payroll export not found."));

        await Should.ThrowAsync<KeyNotFoundException>(() => _controller.Download(Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task ErrorHandlingMiddleware_AnswersAKeyNotFoundWith404()
    {
        var middleware = new Klacks.Api.Infrastructure.Exceptions.ErrorHandlingMiddleware(
            _ => throw new KeyNotFoundException("Payroll export not found."),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<Klacks.Api.Infrastructure.Exceptions.ErrorHandlingMiddleware>>());
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }
}
