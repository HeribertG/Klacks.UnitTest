// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the shared marketplace package download (feature plugins, language packs): a body signed
/// over the identity-bound statement (type, name, SHA-256) is returned byte-exact; a tampered body, a
/// missing signature, a foreign-key signature, a raw-bytes signature, and a signature issued for another
/// name or another artifact type are rejected once a public key is configured; an unsigned body is
/// accepted only while no key is configured (rollout); a declared Content-Length above the cap and a body
/// of unknown length that grows past the cap are both rejected; server errors and network failures are
/// reported as unavailable; a body that stalls after the headers ends as unavailable once the download
/// timeout runs out, while a cancellation by the caller is passed on. The statement format is pinned by a
/// literal shared with the marketplace test.
/// </summary>
using System.Net;
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Marketplace;

[TestFixture]
public class MarketplacePackageDownloaderTests
{
    private const string Url = "https://marketplace.test/api/packages/xx/download";
    private const string ArtifactType = MarketplaceArtifactTypes.LanguagePack;
    private const string ArtifactName = "xx";
    private const long CapBytes = 1024;
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TestGuard = TimeSpan.FromSeconds(10);

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""{ "manifest": { "code": "xx" } }""");

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
    public void Statement_HasTheFormatTheMarketplaceSigns()
    {
        var statement = PackageSignatureStatement.Build("plugin", "Demo-Plugin", Encoding.UTF8.GetBytes("abc"));

        Encoding.UTF8.GetString(statement).ShouldBe(
            "plugin\ndemo-plugin\nba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Test]
    public async Task DownloadAsync_ValidBoundSignature_ReturnsExactBytes()
    {
        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, Payload, SignBound(Payload)));

        result.Status.ShouldBe(MarketplaceDownloadStatus.Succeeded);
        result.Content.ShouldBe(Payload);
    }

    [Test]
    public async Task DownloadAsync_RawBytesSignature_IsRejected()
    {
        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, Payload, _key.Sign(Payload)));

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadAsync_SignatureIssuedForAnotherName_IsRejected()
    {
        var signature = _key.SignBound(ArtifactType, "de", Payload);

        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, Payload, signature));

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadAsync_SignatureIssuedForAnotherArtifactType_IsRejected()
    {
        var signature = _key.SignBound(MarketplaceArtifactTypes.Plugin, ArtifactName, Payload);

        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, Payload, signature));

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadAsync_TamperedBody_IsRejected()
    {
        var tampered = Encoding.UTF8.GetBytes("""{ "manifest": { "code": "yy" } }""");

        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, tampered, SignBound(Payload)));

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
        result.Content.ShouldBeEmpty();
    }

    [Test]
    public async Task DownloadAsync_MissingSignatureWithKeyConfigured_IsRejected()
    {
        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, Payload, signature: null));

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadAsync_SignatureFromForeignKey_IsRejected()
    {
        using var foreignKey = new PackageSigningTestKey();

        var result = await DownloadAsync(new MarketplaceStubHandler(
            HttpStatusCode.OK, Payload, foreignKey.SignBound(ArtifactType, ArtifactName, Payload)));

        result.Status.ShouldBe(MarketplaceDownloadStatus.SignatureRejected);
    }

    [Test]
    public async Task DownloadAsync_UnsignedWithoutConfiguredKey_IsAcceptedForRollout()
    {
        var downloader = new MarketplacePackageDownloader(
            PackageSigningTestKey.CreateVerifier(publicKeyPem: null),
            NullLogger<MarketplacePackageDownloader>.Instance);
        using var client = new HttpClient(new MarketplaceStubHandler(HttpStatusCode.OK, Payload, signature: null));

        var result = await downloader.DownloadAsync(client, Url, ArtifactType, ArtifactName, CapBytes, CancellationToken.None);

        result.Status.ShouldBe(MarketplaceDownloadStatus.Succeeded);
    }

    [Test]
    public async Task DownloadAsync_DeclaredLengthAboveCap_IsRejected()
    {
        var oversized = new byte[CapBytes + 1];

        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.OK, oversized, SignBound(oversized)));

        result.Status.ShouldBe(MarketplaceDownloadStatus.TooLarge);
    }

    [Test]
    public async Task DownloadAsync_UndeclaredLengthGrowingAboveCap_IsRejected()
    {
        var oversized = new byte[CapBytes + 1];
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, oversized, SignBound(oversized), declareLength: false);

        var result = await DownloadAsync(handler);

        result.Status.ShouldBe(MarketplaceDownloadStatus.TooLarge);
    }

    [Test]
    public async Task DownloadAsync_UndeclaredLengthWithinCap_IsAccepted()
    {
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, Payload, SignBound(Payload), declareLength: false);

        var result = await DownloadAsync(handler);

        result.Status.ShouldBe(MarketplaceDownloadStatus.Succeeded);
        result.Content.ShouldBe(Payload);
    }

    [Test]
    public async Task DownloadAsync_ServerError_IsUnavailable()
    {
        var result = await DownloadAsync(new MarketplaceStubHandler(HttpStatusCode.InternalServerError, Payload, SignBound(Payload)));

        result.Status.ShouldBe(MarketplaceDownloadStatus.Unavailable);
    }

    [Test]
    public async Task DownloadAsync_NetworkFailure_IsUnavailable()
    {
        var result = await DownloadAsync(new ThrowingHandler());

        result.Status.ShouldBe(MarketplaceDownloadStatus.Unavailable);
    }

    [Test]
    public async Task DownloadAsync_BodyStallsAfterHeaders_EndsAsUnavailableWhenTheTimeoutRunsOut()
    {
        var downloader = new MarketplacePackageDownloader(
            _key.CreateTrustingVerifier(),
            NullLogger<MarketplacePackageDownloader>.Instance,
            ShortTimeout);
        using var client = new HttpClient(new StallingBodyHandler());

        var download = downloader.DownloadAsync(client, Url, ArtifactType, ArtifactName, CapBytes, CancellationToken.None);
        var finished = await Task.WhenAny(download, Task.Delay(TestGuard));

        finished.ShouldBe(download, "the download must not hang on a stalled body");
        (await download).Status.ShouldBe(MarketplaceDownloadStatus.Unavailable);
    }

    [Test]
    public async Task DownloadAsync_CallerCancelsWhileTheBodyStalls_PassesTheCancellationOn()
    {
        var downloader = new MarketplacePackageDownloader(
            _key.CreateTrustingVerifier(),
            NullLogger<MarketplacePackageDownloader>.Instance,
            TestGuard);
        using var client = new HttpClient(new StallingBodyHandler());
        using var caller = new CancellationTokenSource(ShortTimeout);

        await Should.ThrowAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(client, Url, ArtifactType, ArtifactName, CapBytes, caller.Token));
    }

    private string SignBound(byte[] payload) => _key.SignBound(ArtifactType, ArtifactName, payload);

    private async Task<MarketplacePackageDownload> DownloadAsync(HttpMessageHandler handler)
    {
        var downloader = new MarketplacePackageDownloader(
            _key.CreateTrustingVerifier(),
            NullLogger<MarketplacePackageDownloader>.Instance);
        using var client = new HttpClient(handler);

        return await downloader.DownloadAsync(client, Url, ArtifactType, ArtifactName, CapBytes, CancellationToken.None);
    }

    private sealed class StallingBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream()) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
