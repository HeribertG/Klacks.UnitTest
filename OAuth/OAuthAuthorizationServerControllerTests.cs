// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Controller-level tests of the consent page: the security headers of the page (one value each, replacing what the
/// anti-forgery service already wrote), the language taken from Accept-Language, the access mode that only the POST form
/// carries, the translated error lines, and the decision that must be exactly approve or deny.
/// </summary>

using Klacks.Api.Application.Commands.OAuth;
using Klacks.Api.Application.DTOs.OAuth;
using Klacks.Api.Application.Queries.OAuth;
using Klacks.Api.Application.Services.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.OAuth;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthAuthorizationServerControllerTests
{
    private const string ClientId = "client-1";
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    private const string FrameOptionsName = "X-Frame-Options";

    private IMediator _mediator = null!;
    private IAntiforgery _antiforgery = null!;
    private IInstallationLanguageResolver _installationLanguage = null!;
    private DefaultHttpContext _http = null!;
    private OAuthAuthorizationServerController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        OAuthConsentTexts.Reset();
        _http = new DefaultHttpContext();

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<IRequest<OAuthAuthorizeValidationResult>>(), Arg.Any<CancellationToken>())
            .Returns(OAuthAuthorizeValidationResult.Success(
                new OAuthRedirectMatch("Claude", "claude.ai", OAuthRedirectClass.Vendor, null),
                true,
                TimeSpan.FromDays(OAuthConstants.AccessTokenExpiresInDays),
                RedirectUri));

        _antiforgery = Substitute.For<IAntiforgery>();
        _antiforgery.GetAndStoreTokens(Arg.Any<HttpContext>()).Returns(call =>
        {
            var context = call.Arg<HttpContext>();
            context.Response.Headers[FrameOptionsName] = "SAMEORIGIN";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            return new AntiforgeryTokenSet("request-token", "cookie-token", "__RequestVerificationToken", "RequestVerificationToken");
        });

        _installationLanguage = Substitute.For<IInstallationLanguageResolver>();
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("en");

        _controller = new OAuthAuthorizationServerController(
            _mediator,
            _antiforgery,
            new OAuthConsentTextProvider(_installationLanguage),
            Substitute.For<ILogger<OAuthAuthorizationServerController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = _http }
        };
    }

    [TearDown]
    public void TearDown()
    {
        OAuthConsentTexts.Reset();
    }

    private async Task<string> GetPageAsync()
    {
        var result = await _controller.Authorize(ClientId, RedirectUri, "code", CodeChallenge, "S256", "xyz");

        var content = result.ShouldBeOfType<ContentResult>();
        content.ContentType.ShouldBe("text/html");
        return content.Content!;
    }

    private Task<IActionResult> PostAsync(string? email, string? password, string? decision, string? accessMode)
    {
        return _controller.Authorize(email, password, decision, ClientId, RedirectUri, "code", CodeChallenge, "S256", accessMode, "xyz");
    }

    private void IssueReturns(OAuthAuthorizationCodeResult result)
    {
        _mediator.Send(Arg.Any<IRequest<OAuthAuthorizationCodeResult>>(), Arg.Any<CancellationToken>()).Returns(result);
    }

    [Test]
    public async Task Get_Page_CarriesTheFourSecurityHeaders_WithoutFormAction()
    {
        await GetPageAsync();

        var headers = _http.Response.Headers;
        headers["Content-Security-Policy"].ToString().ShouldBe("frame-ancestors 'none'");
        headers["Content-Security-Policy"].ToString().ShouldNotContain("form-action");
        headers[FrameOptionsName].ToString().ShouldBe("DENY");
        headers.CacheControl.ToString().ShouldBe("no-store");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
    }

    [Test]
    public async Task Get_Page_ReplacesTheAntiforgeryDefaults_SoNoHeaderIsSentTwiceOrConflicts()
    {
        await GetPageAsync();

        var headers = _http.Response.Headers;
        headers[FrameOptionsName].Count.ShouldBe(1);
        headers[FrameOptionsName].ToString().ShouldNotContain("SAMEORIGIN");
        headers.CacheControl.Count.ShouldBe(1);
        headers.CacheControl.ToString().ShouldNotContain("no-cache");
        headers["Content-Security-Policy"].Count.ShouldBe(1);
        headers["Referrer-Policy"].Count.ShouldBe(1);
    }

    [Test]
    public async Task Get_Page_ContainsTheAntiforgeryTokenAsHiddenField()
    {
        var html = await GetPageAsync();

        html.ShouldContain("name=\"__RequestVerificationToken\" value=\"request-token\"");
    }

    [Test]
    public async Task Get_Page_NeverPrefillsTheAccessModeFromTheRequest()
    {
        var html = await GetPageAsync();

        html.ShouldNotContain("type=\"hidden\" name=\"access_mode\"");
        html.ShouldContain("name=\"access_mode\" value=\"read\" checked");
    }

    [Test]
    public async Task Get_WriteNotAllowedByTheValidation_OffersNoWriteOption()
    {
        _mediator.Send(Arg.Any<IRequest<OAuthAuthorizeValidationResult>>(), Arg.Any<CancellationToken>())
            .Returns(OAuthAuthorizeValidationResult.Success(
                new OAuthRedirectMatch("Claude", "claude.ai", OAuthRedirectClass.Vendor, null),
                false,
                TimeSpan.FromDays(OAuthConstants.AccessTokenExpiresInDays),
                RedirectUri));

        var html = await GetPageAsync();

        html.ShouldNotContain("value=\"write\"");
    }

    [Test]
    public async Task Get_AcceptLanguage_PicksTheBestKnownLanguageByQuality()
    {
        _http.Request.Headers.AcceptLanguage = "en;q=0.5, de-CH;q=0.9, xx;q=1.0";

        var html = await GetPageAsync();

        html.ShouldContain("<html lang=\"de\" dir=\"ltr\">");
        html.ShouldContain(OAuthConsentTexts.VariantsOf(OAuthConsentTexts.Title)["de"]);
    }

    [Test]
    public async Task Get_AcceptLanguageWithQualityZero_IsNotUsed()
    {
        _http.Request.Headers.AcceptLanguage = "de;q=0, fr;q=0.4";

        var html = await GetPageAsync();

        html.ShouldContain("<html lang=\"fr\"");
    }

    [Test]
    public async Task Get_NoKnownAcceptLanguage_UsesTheInstallationLanguageThenEnglish()
    {
        _http.Request.Headers.AcceptLanguage = "xx";
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("it");

        (await GetPageAsync()).ShouldContain("<html lang=\"it\"");

        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("yy");

        (await GetPageAsync()).ShouldContain("<html lang=\"en\"");
    }

    [Test]
    public async Task Get_ArabicAcceptLanguage_GivesARightToLeftPage()
    {
        Klacks.Api.Application.Klacksy.AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);
        _http.Request.Headers.AcceptLanguage = "ar-EG,ar;q=0.9";

        var html = await GetPageAsync();

        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
    }

    [Test]
    public async Task Get_InvalidRequest_AnswersBadRequestAndSendsNoPageHeaders()
    {
        _mediator.Send(Arg.Any<IRequest<OAuthAuthorizeValidationResult>>(), Arg.Any<CancellationToken>())
            .Returns(OAuthAuthorizeValidationResult.RejectedWithoutRedirect(OAuthConstants.ErrorInvalidRequest, "nope"));

        var result = await _controller.Authorize(ClientId, RedirectUri, "code", CodeChallenge, "S256", "xyz");

        result.ShouldBeOfType<BadRequestObjectResult>();
        _http.Response.Headers.ContainsKey("Content-Security-Policy").ShouldBeFalse();
    }

    [Test]
    public async Task Post_MissingPassword_ShowsTheTranslatedRequiredLineAndTheHeaders()
    {
        _http.Request.Headers.AcceptLanguage = "de";

        var result = await PostAsync("user@example.com", "", OAuthConstants.DecisionApprove, null);

        var html = result.ShouldBeOfType<ContentResult>().Content!;
        html.ShouldContain(OAuthConsentTexts.VariantsOf(OAuthConsentTexts.ErrorFieldsRequired)["de"]);
        _http.Response.Headers[FrameOptionsName].ToString().ShouldBe("DENY");
        await _mediator.DidNotReceive().Send(Arg.Any<IRequest<OAuthAuthorizationCodeResult>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Post_InvalidCredentials_ShowsTheTranslatedLine()
    {
        _http.Request.Headers.AcceptLanguage = "fr";
        IssueReturns(OAuthAuthorizationCodeResult.Rejected(OAuthConstants.ErrorAccessDenied, OAuthConstants.InvalidCredentialsDescription));

        var result = await PostAsync("user@example.com", "wrong", OAuthConstants.DecisionApprove, null);

        result.ShouldBeOfType<ContentResult>().Content!.ShouldContain(OAuthConsentTexts.VariantsOf(OAuthConsentTexts.ErrorInvalidCredentials)["fr"]);
    }

    [Test]
    public async Task Post_AccountWithoutRight_ShowsTheTranslatedLine()
    {
        _http.Request.Headers.AcceptLanguage = "it";
        IssueReturns(OAuthAuthorizationCodeResult.Rejected(OAuthConstants.ErrorAccessDenied, OAuthConstants.AccountNotAllowedDescription));

        var result = await PostAsync("user@example.com", "secret", OAuthConstants.DecisionApprove, null);

        result.ShouldBeOfType<ContentResult>().Content!.ShouldContain(OAuthConsentTexts.VariantsOf(OAuthConsentTexts.ErrorAccountNotAllowed)["it"]);
    }

    [Test]
    public async Task Post_SetupRequired_ShowsTheTranslatedLine()
    {
        _http.Request.Headers.AcceptLanguage = "de";
        IssueReturns(OAuthAuthorizationCodeResult.SetupRequired());

        var result = await PostAsync("admin@test.com", "secret", OAuthConstants.DecisionApprove, null);

        result.ShouldBeOfType<ContentResult>().Content!.ShouldContain(OAuthConsentTexts.VariantsOf(OAuthConsentTexts.ErrorSetupRequired)["de"]);
    }

    [Test]
    public async Task Post_ApproveWithWriteChosen_PassesTheModeOfTheFormToTheCommand()
    {
        IssueReturns(OAuthAuthorizationCodeResult.Success("the-code"));

        var result = await PostAsync("user@example.com", "secret", OAuthConstants.DecisionApprove, OAuthConstants.AccessModeWrite);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldContain("code=the-code");
        await _mediator.Received(1).Send(
            Arg.Is<IRequest<OAuthAuthorizationCodeResult>>(request =>
                request is IssueOAuthAuthorizationCodeCommand && ((IssueOAuthAuthorizationCodeCommand)request).RequestedAccessMode == OAuthConstants.AccessModeWrite),
            Arg.Any<CancellationToken>());
    }

    [TestCase("")]
    [TestCase("maybe")]
    [TestCase("APPROVE")]
    [TestCase("approve ")]
    public async Task Post_DecisionOtherThanApproveOrDeny_IsRefusedAndIssuesNothing(string decision)
    {
        var result = await PostAsync("user@example.com", "secret", decision, OAuthConstants.AccessModeWrite);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _mediator.DidNotReceive().Send(Arg.Any<IRequest<OAuthAuthorizationCodeResult>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Post_Null_Decision_IsRefused()
    {
        var result = await PostAsync("user@example.com", "secret", null, null);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Test]
    public async Task Post_Deny_RedirectsWithAccessDeniedAndIssuesNothing()
    {
        var result = await PostAsync(null, null, OAuthConstants.DecisionDeny, OAuthConstants.AccessModeWrite);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldContain("error=access_denied");
        await _mediator.DidNotReceive().Send(Arg.Any<IRequest<OAuthAuthorizationCodeResult>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Post_ApproveRedirectWithTheCode_CarriesNoStoreAndNoReferrerHeaders()
    {
        IssueReturns(OAuthAuthorizationCodeResult.Success("the-code"));

        var result = await PostAsync("user@example.com", "secret", OAuthConstants.DecisionApprove, null);

        result.ShouldBeOfType<RedirectResult>();
        AssertRedirectHeaders();
    }

    [Test]
    public async Task Post_DenyRedirectWithAnError_CarriesNoStoreAndNoReferrerHeaders()
    {
        var result = await PostAsync(null, null, OAuthConstants.DecisionDeny, null);

        result.ShouldBeOfType<RedirectResult>();
        AssertRedirectHeaders();
    }

    [Test]
    public async Task Get_RedirectedValidationError_CarriesNoStoreAndNoReferrerHeaders()
    {
        _mediator.Send(Arg.Any<IRequest<OAuthAuthorizeValidationResult>>(), Arg.Any<CancellationToken>())
            .Returns(OAuthAuthorizeValidationResult.RejectedWithRedirect(OAuthConstants.ErrorUnsupportedResponseType, "no", RedirectUri));

        var result = await _controller.Authorize(ClientId, RedirectUri, "token", CodeChallenge, "S256", "xyz");

        result.ShouldBeOfType<RedirectResult>();
        AssertRedirectHeaders();
    }

    private void AssertRedirectHeaders()
    {
        _http.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        _http.Response.Headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        _http.Response.Headers[FrameOptionsName].ToString().ShouldBe("DENY");
    }

    [Test]
    public void ThePostAction_RequiresAnAntiforgeryToken()
    {
        var post = typeof(OAuthAuthorizationServerController).GetMethods()
            .Single(method => method.Name == "Authorize" && method.GetParameters().Length == 10);

        post.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).ShouldNotBeEmpty();
    }

    [Test]
    public void TheHeaderConstants_AreTheOnesOfTheDesign()
    {
        OAuthConsentPageHeaders.ContentSecurityPolicyValue.ShouldBe("frame-ancestors 'none'");
        OAuthConsentPageHeaders.XFrameOptionsValue.ShouldBe("DENY");
        OAuthConsentPageHeaders.CacheControlValue.ShouldBe("no-store");
        OAuthConsentPageHeaders.ReferrerPolicyValue.ShouldBe("no-referrer");
    }

    [Test]
    public void Apply_OverwritesValuesAlreadyOnTheResponse()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers[FrameOptionsName] = "SAMEORIGIN";
        context.Response.Headers.CacheControl = "public, max-age=60";

        OAuthConsentPageHeaders.Apply(context.Response);

        context.Response.Headers[FrameOptionsName].ToString().ShouldBe("DENY");
        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
    }
}
