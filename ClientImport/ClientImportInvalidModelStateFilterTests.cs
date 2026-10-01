// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A Preview/Commit body the framework cannot bind (e.g. an unknown enum name) must be rejected with the
/// import's invalid-request code before ASP.NET's own code-less 400 answers it, and without leaking the
/// JSON library's message.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportInvalidModelStateFilterTests
{
    private const string RawJsonError = "The JSON value could not be converted to Klacks.Api.Domain.Enums.ClientImportEmailType.";

    [Test]
    public void InvalidBody_IsRejectedWithTheInvalidRequestCode()
    {
        var context = ExecutingContext();
        context.ModelState.AddModelError("$.policy.emailType", RawJsonError);

        var exception = Should.Throw<ClientImportRejectedException>(() => new ClientImportInvalidModelStateFilterAttribute().OnActionExecuting(context));

        exception.Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);
        exception.Message.ShouldContain("$.policy.emailType");
        exception.Message.ShouldNotContain("could not be converted");
    }

    [Test]
    public void ValidBody_PassesThrough()
    {
        var context = ExecutingContext();

        Should.NotThrow(() => new ClientImportInvalidModelStateFilterAttribute().OnActionExecuting(context));
        context.Result.ShouldBeNull();
    }

    [Test]
    public void Filter_RunsBeforeTheFrameworksModelStateFilter()
    {
        var frameworkFilter = new ModelStateInvalidFilter(
            new ApiBehaviorOptions { InvalidModelStateResponseFactory = _ => new BadRequestResult() }, NullLogger.Instance);

        new ClientImportInvalidModelStateFilterAttribute().Order.ShouldBeLessThan(frameworkFilter.Order);
    }

    private static ActionExecutingContext ExecutingContext()
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }
}
