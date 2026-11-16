// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Services.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Presentation.Controllers.OAuth;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthLoginPageRendererTests
{
    private static readonly OAuthRedirectMatch ClaudeMatch = new("Claude", "claude.ai", OAuthRedirectClass.Vendor, null);
    private static readonly OAuthRedirectMatch LoopbackMatch = new(OAuthTrustedRedirects.LocalProgramDisplayName, "127.0.0.1", OAuthRedirectClass.Loopback, 33418);

    [SetUp]
    public void SetUp()
    {
        OAuthConsentTexts.Reset();
    }

    [TearDown]
    public void TearDown()
    {
        OAuthConsentTexts.Reset();
    }

    private static OAuthConsentTextSet Texts(string language)
    {
        var texts = OAuthConsentTexts.RequiredKeys.ToDictionary(
            key => key,
            key => OAuthConsentTexts.TryGetText(key, language, out var text) ? text : OAuthConsentTexts.EnglishOf(key),
            StringComparer.Ordinal);
        return new OAuthConsentTextSet(language, Klacks.Api.Domain.Common.LanguageTag.IsRightToLeft(language), texts);
    }

    private static OAuthConsentPageModel Model(
        OAuthRedirectMatch? match = null,
        bool writeAllowed = true,
        TimeSpan? lifetime = null,
        string language = "en",
        IReadOnlyDictionary<string, string>? hidden = null,
        string? error = null)
    {
        return new OAuthConsentPageModel(
            "/oauth/authorize",
            match ?? ClaudeMatch,
            writeAllowed,
            lifetime ?? TimeSpan.FromDays(OAuthConstants.AccessTokenExpiresInDays),
            Texts(language),
            hidden ?? new Dictionary<string, string> { ["client_id"] = "client-1" },
            error);
    }

    [Test]
    public void Render_ShowsToolNameAndHostFromTheListEntry()
    {
        var html = OAuthLoginPageRenderer.Render(Model());

        html.ShouldContain("<div class=\"toolname\">Claude</div>");
        html.ShouldContain("<div class=\"toolhost\" dir=\"ltr\">claude.ai</div>");
        html.ShouldContain("<title>Connect an external AI tool to Klacks</title>");
    }

    [Test]
    public void Render_OfferedFieldsAndDecisionValues_AreTheOnesTheControllerReads()
    {
        var html = OAuthLoginPageRenderer.Render(Model());

        html.ShouldContain("name=\"access_mode\" value=\"read\" checked");
        html.ShouldContain("name=\"access_mode\" value=\"write\"");
        html.ShouldContain("name=\"email\"");
        html.ShouldContain("name=\"password\"");
        html.ShouldContain("name=\"decision\" value=\"approve\"");
        html.ShouldContain("name=\"decision\" value=\"deny\"");
    }

    [Test]
    public void Render_ReadIsTheOnlyPreselectedOption()
    {
        var html = OAuthLoginPageRenderer.Render(Model());

        System.Text.RegularExpressions.Regex.Matches(html, " checked").Count.ShouldBe(1);
        html.IndexOf("value=\"read\" checked", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("value=\"write\"", StringComparison.Ordinal));
        html.ShouldNotContain("value=\"write\" checked");
    }

    [Test]
    public void Render_WriteSwitchOff_OffersNoWriteOptionAtAll()
    {
        var html = OAuthLoginPageRenderer.Render(Model(writeAllowed: false));

        html.ShouldNotContain("value=\"write\"");
        html.ShouldNotContain(OAuthConsentTexts.EnglishOf(OAuthConsentTexts.AccessWrite));
        html.ShouldContain("value=\"read\" checked");
    }

    [Test]
    public void Render_WriteSwitchOn_OffersTheWriteSentence()
    {
        OAuthLoginPageRenderer.Render(Model(writeAllowed: true))
            .ShouldContain(OAuthConsentTexts.EnglishOf(OAuthConsentTexts.AccessWrite));
    }

    [Test]
    public void Render_ReadAndWriteSentences_TellInPlainWordsWhatTheToolMayDo()
    {
        var html = OAuthLoginPageRenderer.Render(Model());

        html.ShouldContain("Read only: look at plans, employees and other data, but change nothing");
        html.ShouldContain("Read and change: also change data in your name");
    }

    [Test]
    public void Render_Lifetime_ThirtyDaysOutsideThePlaygroundAndTwentyFourHoursOnIt()
    {
        var normal = OAuthLoginPageRenderer.Render(Model(lifetime: TimeSpan.FromDays(OAuthConstants.AccessTokenExpiresInDays)));
        var playground = OAuthLoginPageRenderer.Render(Model(lifetime: TimeSpan.FromHours(OAuthConstants.PlaygroundAccessTokenExpiresInHours)));

        normal.ShouldContain("valid for 30 days");
        normal.ShouldNotContain("24 hours");
        playground.ShouldContain("valid for 24 hours");
        playground.ShouldNotContain("30 days");
    }

    [Test]
    public void Render_VendorTarget_HasNoLocalProgramWarning()
    {
        OAuthLoginPageRenderer.Render(Model()).ShouldNotContain("class=\"warning\"");
    }

    [Test]
    public void Render_LoopbackTarget_WarnsThatAProgramOnThisComputerReceivesAccess_NamingHostAndPort()
    {
        var html = OAuthLoginPageRenderer.Render(Model(match: LoopbackMatch));

        html.ShouldContain("class=\"warning\"");
        html.ShouldContain("This is a program on this computer (127.0.0.1:33418).");
        html.ShouldContain("<div class=\"toolname\">Program on this computer</div>");
        html.ShouldContain("<div class=\"toolhost\" dir=\"ltr\">127.0.0.1:33418</div>");
        html.ShouldNotContain("{target}");
    }

    [Test]
    public void Render_LoopbackWithoutPort_NamesTheHostOnly()
    {
        var html = OAuthLoginPageRenderer.Render(Model(match: new OAuthRedirectMatch("Program on this computer", "vscode.dev", OAuthRedirectClass.Loopback, null)));

        html.ShouldContain("This is a program on this computer (vscode.dev).");
    }

    [Test]
    public void Render_CustomTarget_ShowsItsHostAsTheName()
    {
        var html = OAuthLoginPageRenderer.Render(Model(match: new OAuthRedirectMatch("mcp.example.org", "mcp.example.org", OAuthRedirectClass.Custom, null)));

        html.ShouldContain("<div class=\"toolname\">mcp.example.org</div>");
        html.ShouldNotContain("class=\"warning\"");
    }

    [Test]
    public void Render_HostileValues_AreEncodedEverywhere()
    {
        const string script = "<script>alert(1)</script>";
        var hostile = new OAuthRedirectMatch(script, script + "\"onmouseover=\"x", OAuthRedirectClass.Custom, null);
        var hidden = new Dictionary<string, string> { ["state"] = script, ["redirect_uri\"><x"] = "\"><script>alert(2)</script>" };

        var html = OAuthLoginPageRenderer.Render(Model(match: hostile, hidden: hidden, error: script));

        html.ShouldNotContain("<script>");
        html.ShouldNotContain("onmouseover=\"x");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Test]
    public void Render_ClientControlledHiddenValuesWithPlaceholders_AreNeverTreatedAsPlaceholders()
    {
        var hidden = new Dictionary<string, string> { ["state"] = "{{DENY}}{{ALLOW}}{{TOOL_NAME}}" };

        var html = OAuthLoginPageRenderer.Render(Model(hidden: hidden));

        html.ShouldContain("value=\"{{DENY}}{{ALLOW}}{{TOOL_NAME}}\"");
    }

    [Test]
    public void Render_ToolNameContainingAnotherPlaceholder_StaysLiteralAndTheRealPlaceholderIsStillFilled()
    {
        var hostile = new OAuthRedirectMatch("{{INTRO}}{{ALLOW}}{{HIDDEN_FIELDS}}", "{{DENY}}.example.org", OAuthRedirectClass.Custom, null);

        var html = OAuthLoginPageRenderer.Render(Model(match: hostile));

        html.ShouldContain("<div class=\"toolname\">{{INTRO}}{{ALLOW}}{{HIDDEN_FIELDS}}</div>");
        html.ShouldContain("<div class=\"toolhost\" dir=\"ltr\">{{DENY}}.example.org</div>");
        html.ShouldContain($"<p class=\"consent\">{OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Intro)}</p>");
        html.ShouldContain($">{OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Allow)}</button>");
        html.ShouldContain("<input type=\"hidden\" name=\"client_id\" value=\"client-1\">");
    }

    [Test]
    public void Render_ErrorMessageContainingAnotherPlaceholder_StaysLiteralAndTheRealPlaceholderIsStillFilled()
    {
        var html = OAuthLoginPageRenderer.Render(Model(error: "{{ALLOW}} {{DENY}} {{FORM_ACTION}}"));

        html.ShouldContain("<div class=\"error\">{{ALLOW}} {{DENY}} {{FORM_ACTION}}</div>");
        html.ShouldContain($">{OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Allow)}</button>");
        html.ShouldContain($">{OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Deny)}</button>");
        html.ShouldContain("<form method=\"post\" action=\"/oauth/authorize\">");
    }

    [Test]
    public void Render_HiddenFieldNameAndValueContainingPlaceholders_StayLiteral()
    {
        var hidden = new Dictionary<string, string> { ["{{VALUE}}"] = "{{NAME}}" };

        var html = OAuthLoginPageRenderer.Render(Model(hidden: hidden));

        html.ShouldContain("<input type=\"hidden\" name=\"{{VALUE}}\" value=\"{{NAME}}\">");
    }

    [Test]
    public void Render_EveryPlaceholderOfTheTemplateHasAValue_SoNoneIsLeftUnresolved()
    {
        var html = OAuthLoginPageRenderer.Render(Model(match: LoopbackMatch, error: "Invalid email or password."));

        System.Text.RegularExpressions.Regex.IsMatch(html, @"\{\{[A-Z_]+\}\}").ShouldBeFalse();
    }

    [Test]
    public void Render_ClientNameNeverAppears_BecauseTheModelHasNoSuchField()
    {
        typeof(OAuthConsentPageModel).GetProperties().Select(p => p.Name).ShouldNotContain("ClientName");
        OAuthLoginPageRenderer.Render(Model()).ShouldNotContain("Klacks Official");
    }

    [Test]
    public void Render_HiddenFields_CarryTheOAuthParametersAndSkipEmptyOnes()
    {
        var hidden = new Dictionary<string, string> { ["client_id"] = "client-1", ["state"] = string.Empty };

        var html = OAuthLoginPageRenderer.Render(Model(hidden: hidden));

        html.ShouldContain("<input type=\"hidden\" name=\"client_id\" value=\"client-1\">");
        html.ShouldNotContain("name=\"state\"");
    }

    [Test]
    public void Render_ErrorMessage_IsShownOnlyWhenGiven()
    {
        OAuthLoginPageRenderer.Render(Model()).ShouldNotContain("class=\"error\"");
        OAuthLoginPageRenderer.Render(Model(error: "Invalid email or password.")).ShouldContain("<div class=\"error\">Invalid email or password.</div>");
    }

    [TestCase("en", "en", "ltr")]
    [TestCase("de", "de", "ltr")]
    [TestCase("fr", "fr", "ltr")]
    [TestCase("it", "it", "ltr")]
    public void Render_CoreLanguages_SetLangAndLeftToRight(string language, string lang, string dir)
    {
        var html = OAuthLoginPageRenderer.Render(Model(language: language));

        html.ShouldContain($"<html lang=\"{lang}\" dir=\"{dir}\">");
    }

    [TestCase("ar")]
    [TestCase("he")]
    public void Render_ArabicAndHebrew_AreRightToLeftWhileTheHostStaysLeftToRight(string language)
    {
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);

        var html = OAuthLoginPageRenderer.Render(Model(language: language));

        html.ShouldContain($"<html lang=\"{language}\" dir=\"rtl\">");
        html.ShouldContain("<div class=\"toolhost\" dir=\"ltr\">claude.ai</div>");
    }

    [Test]
    public void Render_NonLatinText_IsWrittenAsItIsNotAsNumericEntities()
    {
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);

        var html = OAuthLoginPageRenderer.Render(Model(language: "ja"));

        html.ShouldContain("外部のAIツールをKlacksに接続");
        html.ShouldNotContain("&#x");
    }

    [Test]
    public void Render_EveryLanguage_FillsEveryPlaceholderAndLeavesNoTemplateMarkerBehind()
    {
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);
        var languages = new List<string>(OAuthConsentTexts.CoreLanguages);
        foreach (var directory in Directory.GetDirectories(Path.Combine(RepositoryRootLocator.ApiProject, "Plugins", "Languages")))
        {
            var code = Path.GetFileName(directory);
            if (!languages.Contains(code))
            {
                languages.Add(code);
            }
        }

        foreach (var language in languages)
        {
            var html = OAuthLoginPageRenderer.Render(Model(match: LoopbackMatch, language: language));

            html.ShouldNotContain("{{", customMessage: language);
            html.ShouldNotContain("{target}", customMessage: language);
            html.ShouldNotContain("{duration}", customMessage: language);
            html.ShouldContain("127.0.0.1:33418", customMessage: language);
        }

        languages.Count.ShouldBe(25);
    }
}
