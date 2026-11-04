// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Language-pack download path of MarketplaceClientService: it requests the download endpoint of the
/// package and hands back the bundle only when the vendor signature over the exact bytes verifies; a
/// tampered bundle, a missing signature, a foreign-key signature and a bundle signed for another language
/// never reach the caller.
/// </summary>
using System.Net;
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Marketplace;

[TestFixture]
public class MarketplaceClientServiceDownloadTests
{
    private const string BaseUrl = "https://marketplace.test";
    private const string Code = "xx";

    private static readonly byte[] Bundle = Encoding.UTF8.GetBytes("""{ "manifest": { "code": "xx" }, "translations": {} }""");

    private PackageSigningTestKey _key = null!;

    [SetUp]
    public void SetUp()
    {
        _key = new PackageSigningTestKey();
    }

    [TearDown]
    public void TearDown()
    {
        _key.Dispose();
    }

    [Test]
    public async Task DownloadPackageAsync_ValidSignature_ReturnsBundleFromTheDownloadEndpoint()
    {
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, Bundle, _key.SignBound(MarketplaceArtifactTypes.LanguagePack, Code, Bundle));

        var result = await CreateService(handler).DownloadPackageAsync(Code, CancellationToken.None);

        result.Status.ShouldBe(MarketplaceDownloadStatus.Succeeded);
        result.Content.ShouldBe(Bundle);
        handler.LastRequestUri!.ToString().ShouldBe($"{BaseUrl}/api/packages/{Code}/download");
    }

    [Test]
    public async Task DownloadPackageAsync_TamperedBundle_IsRejected()
    {
        var tampered = Encoding.UTF8.GetBytes("""{ "manifest": { "code": "xx" }, "translations": { "a": "b" } }""");
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, tampered, _key.SignBound(MarketplaceArtifactTypes.LanguagePack, Code, Bundle));

        var result = await CreateService(handler).DownloadPackageAsync(Code, CancellationToken.None);

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadPackageAsync_MissingSignature_IsRejected()
    {
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, Bundle, signature: null);

        var result = await CreateService(handler).DownloadPackageAsync(Code, CancellationToken.None);

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadPackageAsync_ForeignKeySignature_IsRejected()
    {
        using var foreignKey = new PackageSigningTestKey();
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, Bundle, foreignKey.SignBound(MarketplaceArtifactTypes.LanguagePack, Code, Bundle));

        var result = await CreateService(handler).DownloadPackageAsync(Code, CancellationToken.None);

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadPackageAsync_BundleSignedForAnotherLanguage_IsRejected()
    {
        var handler = new MarketplaceStubHandler(
            HttpStatusCode.OK, Bundle, _key.SignBound(MarketplaceArtifactTypes.LanguagePack, "de", Bundle));

        var result = await CreateService(handler).DownloadPackageAsync(Code, CancellationToken.None);

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    private MarketplaceClientService CreateService(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanguagePlugins:MarketplaceUrl"] = BaseUrl
            })
            .Build();

        var downloader = new MarketplacePackageDownloader(
            _key.CreateTrustingVerifier(),
            NullLogger<MarketplacePackageDownloader>.Instance);

        return new MarketplaceClientService(
            new HttpClient(handler),
            configuration,
            downloader,
            NullLogger<MarketplaceClientService>.Instance);
    }
}
