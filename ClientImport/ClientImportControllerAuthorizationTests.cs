// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Access to the employee import: every action is Admin only with the JWT scheme pinned (owner decision
/// 2026-10-01), Preview and Commit are rate limited per user, the upload is buffered in memory, and a
/// request without a file is rejected with a code instead of failing on a null file. The action reads the
/// form itself, so an oversized or broken upload ends in a coded rejection instead of the framework's
/// code-less model-binding 400; the template language falls back to the Accept-Language header.
/// </summary>

using System.Reflection;
using System.Text;
using Klacks.Api.Application.Commands.ClientImport;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.ClientImport;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Queries.ClientImport;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Primitives;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportControllerAuthorizationTests
{
    private static readonly string[] Actions =
        [nameof(ClientImportController.Parse), nameof(ClientImportController.Preview), nameof(ClientImportController.Commit), nameof(ClientImportController.Template)];

    [TestCaseSource(nameof(Actions))]
    public void EveryAction_IsAdminOnly_WithTheJwtScheme(string actionName)
    {
        var attributes = Action(actionName).GetCustomAttributes<AuthorizeAttribute>().ToList();

        attributes.ShouldNotBeEmpty();
        attributes.ShouldAllBe(a => a.AuthenticationSchemes == JwtBearerDefaults.AuthenticationScheme && a.Roles == Roles.Admin);
    }

    [TestCase(nameof(ClientImportController.Preview))]
    [TestCase(nameof(ClientImportController.Commit))]
    public void PreviewAndCommit_AreRateLimited(string actionName)
    {
        Action(actionName).GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.ShouldBe(RateLimitingPolicies.ClientImport);
    }

    [Test]
    public void Upload_IsBufferedInMemory()
    {
        var limits = Action(nameof(ClientImportController.Parse)).GetCustomAttribute<RequestFormLimitsAttribute>()!;

        limits.MemoryBufferThreshold.ShouldBeGreaterThanOrEqualTo((int)ClientImportLimits.MaxUploadRequestBytes);
    }

    [Test]
    public async Task MissingFile_IsRejectedWithACode()
    {
        var mediator = Substitute.For<IMediator>();
        var controller = ControllerWithForm(mediator, new FormCollection([]));

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(() => controller.Parse());

        exception.Code.ShouldBe(ClientImportErrorCodes.FileEmpty);
    }

    [Test]
    public async Task UploadedFile_AndSheetName_AreReadFromTheForm()
    {
        var mediator = Substitute.For<IMediator>();
        ParseClientImportCommand? sent = null;
        mediator.Send(Arg.Do<ParseClientImportCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns(new ClientImportParseResult());
        byte[] content = [1, 2, 3];
        var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "staff.csv");
        var form = new FormCollection(
            new Dictionary<string, StringValues> { ["sheetName"] = "Personal" },
            new FormFileCollection { file });

        await ControllerWithForm(mediator, form).Parse();

        sent.ShouldNotBeNull();
        sent.Content.ShouldBe(content);
        sent.FileName.ShouldBe("staff.csv");
        sent.SheetName.ShouldBe("Personal");
    }

    [Test]
    public async Task RequestAboveTheServerBodyLimit_IsRejectedAsFileTooLarge()
    {
        var mediator = Substitute.For<IMediator>();
        var controller = ControllerWithFormFeature(mediator, new ThrowingFormFeature(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)));

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(() => controller.Parse());

        exception.Code.ShouldBe(ClientImportErrorCodes.FileTooLarge);
        await mediator.DidNotReceiveWithAnyArgs().Send(Arg.Any<ParseClientImportCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MultipartBodyAboveTheFormLimit_IsRejectedAsFileTooLarge()
    {
        var mediator = Substitute.For<IMediator>();
        const string boundary = "b0undary";
        var body = $"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.csv\"\r\nContent-Type: text/csv\r\n\r\n"
                   + new string('x', 4096) + $"\r\n--{boundary}--\r\n";
        var context = new DefaultHttpContext();
        context.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        context.Request.Body = new MemoryStream(Encoding.ASCII.GetBytes(body));
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions { MultipartBodyLengthLimit = 1024 }));
        var controller = new ClientImportController(mediator) { ControllerContext = new ControllerContext { HttpContext = context } };

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(() => controller.Parse());

        exception.Code.ShouldBe(ClientImportErrorCodes.FileTooLarge);
    }

    [Test]
    public async Task OtherBadRequest_WhileReadingTheForm_IsAnInvalidRequest()
    {
        var mediator = Substitute.For<IMediator>();
        var controller = ControllerWithFormFeature(mediator, new ThrowingFormFeature(
            new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest)));

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(() => controller.Parse());

        exception.Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);
    }

    [Test]
    public void Parse_BindsNoFormParameters_SoTheFrameworkCannotAnswerWithoutACode()
    {
        Action(nameof(ClientImportController.Parse)).GetParameters().ShouldBeEmpty();
    }

    [Test]
    public async Task Template_WithoutLanguage_PassesTheAcceptLanguagesByQuality()
    {
        var mediator = Substitute.For<IMediator>();
        GetClientImportTemplateQuery? sent = null;
        mediator.Send(Arg.Do<GetClientImportTemplateQuery>(q => sent = q), Arg.Any<CancellationToken>())
            .Returns(new ClientImportTemplateFile([1], "t.xlsx", "application/octet-stream"));
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "fr-CH;q=0.8, de-CH, en;q=0.5";
        var controller = new ClientImportController(mediator) { ControllerContext = new ControllerContext { HttpContext = context } };

        await controller.Template(null, CancellationToken.None);

        sent.ShouldNotBeNull();
        sent.Language.ShouldBeNull();
        sent.PreferredLanguages.ShouldBe(["de-CH", "fr-CH", "en"]);
    }

    [Test]
    public void Template_LanguageIsOptional()
    {
        var parameter = Action(nameof(ClientImportController.Template)).GetParameters().First(p => p.ParameterType == typeof(string));

        new NullabilityInfoContext().Create(parameter).WriteState.ShouldBe(NullabilityState.Nullable);
    }

    [Test]
    public void OnlyTheImportController_AnswersInvalidBodiesWithACode()
    {
        typeof(ClientImportController).GetCustomAttribute<ClientImportInvalidModelStateFilterAttribute>().ShouldNotBeNull();

        var others = typeof(ClientImportController).Assembly.GetTypes()
            .Where(t => t != typeof(ClientImportController) && t.GetCustomAttribute<ClientImportInvalidModelStateFilterAttribute>(inherit: true) != null)
            .ToList();
        others.ShouldBeEmpty();
    }

    private static ClientImportController ControllerWithForm(IMediator mediator, IFormCollection form) =>
        ControllerWithFormFeature(mediator, new FormFeature(form));

    private static ClientImportController ControllerWithFormFeature(IMediator mediator, IFormFeature feature)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Features.Set(feature);
        return new ClientImportController(mediator) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class ThrowingFormFeature(Exception exception) : IFormFeature
    {
        public bool HasFormContentType => true;

        public IFormCollection? Form { get; set; }

        public IFormCollection ReadForm() => throw exception;

        public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) => Task.FromException<IFormCollection>(exception);
    }

    private static MethodInfo Action(string name) => typeof(ClientImportController).GetMethod(name)!;
}
