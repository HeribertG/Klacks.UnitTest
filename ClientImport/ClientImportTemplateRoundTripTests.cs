// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The downloadable template must be recognised completely by the import in every supported language:
/// the template is written with the real xlsx builder, read back with the real xlsx reader and every
/// column must be detected as its intended target with full confidence.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.ClientImport;
using Klacks.Api.Application.Queries.ClientImport;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Services.ClientImport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportTemplateRoundTripTests
{
    private const int CurrentYear = 2026;

    private ClientImportSynonymCatalog _catalog = null!;

    [OneTimeSetUp]
    public void LoadCatalog()
    {
        _catalog = ClientImportSynonymCatalog.LoadEmbedded();
    }

    [TestCaseSource(typeof(ClientImportSynonymCatalogTests), nameof(ClientImportSynonymCatalogTests.SupportedLanguages))]
    public void Template_IsDetectedCompletely(string language)
    {
        var headers = ClientImportTemplateLayout.Headers(_catalog, language);
        var content = new ClientImportTemplateBuilder().Build(ClientImportTemplateLayout.SheetName, headers);

        var sheet = new XlsxClientImportFileReader().Read(content, null);

        sheet.Rows.Count.ShouldBe(1);
        ClientImportHeaderRowLocator.Locate(sheet.Rows).ShouldBe(0);

        var mapping = new ClientImportColumnDetector(_catalog).Detect(sheet.Rows[0], [], CurrentYear);

        mapping.Select(m => m.Target).ShouldBe(ClientImportTemplateLayout.Targets, $"language {language}");
        mapping.ShouldAllBe(m => m.Confidence == ClientImportColumnDetector.ExactHeaderConfidence);
    }

    [Test]
    public void Template_HasTheHeadersOfTheRequestedLanguage()
    {
        var headers = ClientImportTemplateLayout.Headers(_catalog, "de");

        headers[0].ShouldBe("Vorname");
        headers[1].ShouldBe("Nachname");
        headers.ShouldContain("PLZ");
        ClientImportTemplateLayout.Headers(_catalog, "ja").ShouldContain("郵便番号");
        ClientImportTemplateLayout.Headers(_catalog, "zh-CN").ShouldContain("邮政编码");
    }

    [Test]
    public async Task TemplateHandler_MatchesTheLanguageCaseInsensitively()
    {
        var handler = new GetClientImportTemplateQueryHandler(_catalog, new ClientImportTemplateBuilder(),
            NullLogger<GetClientImportTemplateQueryHandler>.Instance);

        var file = await handler.Handle(new GetClientImportTemplateQuery("ZH-cn"), CancellationToken.None);

        file.FileName.ShouldBe("klacks-employee-import-zh-CN.xlsx");
        file.ContentType.ShouldBe(ClientImportTemplateLayout.ContentType);
        new XlsxClientImportFileReader().Read(file.Content, null).Rows[0][0].ShouldBe("名");
    }

    [Test]
    public async Task TemplateHandler_RejectsAnUnsupportedLanguage()
    {
        var handler = new GetClientImportTemplateQueryHandler(_catalog, new ClientImportTemplateBuilder(),
            NullLogger<GetClientImportTemplateQueryHandler>.Instance);

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(
            () => handler.Handle(new GetClientImportTemplateQuery("xx"), CancellationToken.None));

        exception.Code.ShouldBe(ClientImportErrorCodes.UnsupportedLanguage);
    }

    [TestCase(null, new string[0], "en")]
    [TestCase("", new string[0], "en")]
    [TestCase("  ", new[] { "fr-CH", "de" }, "fr")]
    [TestCase(null, new[] { "de-CH", "en" }, "de")]
    [TestCase(null, new[] { "zh-TW", "en" }, "zh-TW")]
    [TestCase(null, new[] { "xx-YY", "it" }, "it")]
    [TestCase(null, new[] { "xx" }, "en")]
    [TestCase("nl", new[] { "de" }, "nl")]
    public async Task TemplateHandler_WithoutLanguage_UsesTheFirstSupportedPreferredLanguageOrEnglish(
        string? language, string[] preferred, string expected)
    {
        var handler = new GetClientImportTemplateQueryHandler(_catalog, new ClientImportTemplateBuilder(),
            NullLogger<GetClientImportTemplateQueryHandler>.Instance);

        var file = await handler.Handle(new GetClientImportTemplateQuery(language, preferred), CancellationToken.None);

        file.FileName.ShouldBe($"klacks-employee-import-{expected}.xlsx");
    }

    [Test]
    public void Template_TargetsNeverIncludeIgnoredOrCombinedColumns()
    {
        ClientImportTemplateLayout.Targets.ShouldNotContain(ClientImportTarget.Ignore);
        ClientImportTemplateLayout.Targets.ShouldNotContain(ClientImportTarget.PersonnelNumber);
        ClientImportTemplateLayout.Targets.ShouldNotContain(ClientImportTarget.FullName);
        ClientImportTemplateLayout.Targets.Distinct().Count().ShouldBe(ClientImportTemplateLayout.Targets.Count);
    }
}
