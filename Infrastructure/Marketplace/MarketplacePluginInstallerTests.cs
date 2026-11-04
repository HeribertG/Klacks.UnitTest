// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end tests of the marketplace plugin installation with a real RSA key pair, the real marketplace
/// client, the real download cap, the real zip guard and real file system work: a correctly signed plugin
/// replaces its directory and is installed; a tampered ZIP, a missing signature, a foreign-key signature
/// and a ZIP signed for another plugin are rejected before anything touches the disk; a zip bomb, too many
/// entries, a body that is no archive and a manifest naming another plugin are rejected before extraction;
/// an extraction that fails half-way (entry escaping the directory) leaves the previous plugin directory
/// untouched and no staging directory behind; an invalid name never reaches the marketplace.
/// </summary>
using System.IO.Compression;
using System.Net;
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Plugins;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Infrastructure.Services.Plugins;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Marketplace;

[TestFixture]
public class MarketplacePluginInstallerTests
{
    private const string BaseUrl = "https://marketplace.test/";
    private const string PluginName = "demo-plugin";
    private const string ManifestEntry = "manifest.json";
    private const string StaleFile = "stale.txt";
    private const int BombEntryBytes = 8 * 1024 * 1024;

    private PackageSigningTestKey _key = null!;
    private IFeaturePluginService _featurePluginService = null!;
    private string _workRoot = null!;
    private string _pluginRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _key = new PackageSigningTestKey();
        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _featurePluginService.InstallAsync(PluginName).Returns(true);
        _workRoot = Path.Combine(Path.GetTempPath(), "klacks-plugin-install-" + Guid.NewGuid().ToString("N"));
        _pluginRoot = Path.Combine(_workRoot, "Features");
        Directory.CreateDirectory(_pluginRoot);
    }

    [TearDown]
    public void TearDown()
    {
        _key.Dispose();
        if (Directory.Exists(_workRoot))
        {
            Directory.Delete(_workRoot, recursive: true);
        }
    }

    [Test]
    public async Task InstallAsync_ValidSignature_ReplacesTheDirectoryAndInstalls()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_pluginRoot, PluginName)).FullName;
        File.WriteAllText(Path.Combine(existing, StaleFile), "old");
        var zip = BuildZip(Manifest(PluginName));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.Installed);
        File.Exists(Path.Combine(_pluginRoot, PluginName, ManifestEntry)).ShouldBeTrue();
        File.Exists(Path.Combine(_pluginRoot, PluginName, StaleFile)).ShouldBeFalse();
        AssertNoWorkDirectoriesLeft();
        handler.LastRequestUri!.ToString().ShouldBe($"{BaseUrl}api/plugins/{PluginName}/download");
        await _featurePluginService.Received(1).RefreshPluginsAsync();
        await _featurePluginService.Received(1).InstallAsync(PluginName);
    }

    [Test]
    public async Task InstallAsync_ZipSignedForAnotherPlugin_IsRejected()
    {
        var zip = BuildZip(Manifest(PluginName));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin("other-plugin", zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.SignatureRejected);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_TamperedZip_IsRejectedBeforeExtraction()
    {
        var original = BuildZip(Manifest(PluginName));
        var tampered = BuildZip(Manifest(PluginName), ("extra.txt", [1]));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, tampered, SignPlugin(PluginName, original));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.SignatureRejected);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_MissingSignature_IsRejected()
    {
        var zip = BuildZip(Manifest(PluginName));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, signature: null);

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.SignatureRejected);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_ForeignKeySignature_IsRejected()
    {
        using var foreignKey = new PackageSigningTestKey();
        var zip = BuildZip(Manifest(PluginName));
        var handler = new MarketplaceStubHandler(
            HttpStatusCode.OK, zip, foreignKey.SignBound(MarketplaceArtifactTypes.Plugin, PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.SignatureRejected);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_ManifestNamingAnotherPlugin_IsRejectedBeforeExtraction()
    {
        var zip = BuildZip(Manifest("other-plugin"));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.ManifestMismatch);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_ArchiveWithoutManifest_IsRejectedBeforeExtraction()
    {
        var zip = BuildZip(("readme.txt", Encoding.UTF8.GetBytes("no manifest")));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.ManifestMismatch);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_ExtractionFailingHalfWay_KeepsThePreviousDirectory()
    {
        var existing = Directory.CreateDirectory(Path.Combine(_pluginRoot, PluginName)).FullName;
        File.WriteAllText(Path.Combine(existing, StaleFile), "old");
        var zip = BuildZip(Manifest(PluginName), ("../escape.txt", [1]));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.InvalidArchive);
        File.ReadAllText(Path.Combine(existing, StaleFile)).ShouldBe("old");
        File.Exists(Path.Combine(existing, ManifestEntry)).ShouldBeFalse();
        File.Exists(Path.Combine(_workRoot, "escape.txt")).ShouldBeFalse();
        AssertNoWorkDirectoriesLeft();
        await _featurePluginService.DidNotReceive().InstallAsync(Arg.Any<string>());
    }

    [Test]
    public async Task InstallAsync_SignedZipBomb_IsRejectedBeforeExtraction()
    {
        var zip = BuildZip(Manifest(PluginName), ("bomb.bin", new byte[BombEntryBytes]));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.InvalidArchive);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_SignedZipWithTooManyEntries_IsRejectedBeforeExtraction()
    {
        var entries = Enumerable.Range(0, MarketplacePackageLimits.MaxPluginZipEntries + 1)
            .Select(index => ($"file-{index}.txt", new byte[] { 1 }))
            .Prepend(Manifest(PluginName))
            .ToArray();
        var zip = BuildZip(entries);
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.InvalidArchive);
        AssertNothingInstalled();
    }

    [Test]
    public async Task InstallAsync_SignedBodyThatIsNoArchive_IsRejected()
    {
        var body = Encoding.UTF8.GetBytes("not a zip");
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, body, SignPlugin(PluginName, body));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.InvalidArchive);
        AssertNothingInstalled();
    }

    [TestCase("../evil")]
    [TestCase("evil\n")]
    [TestCase("")]
    public async Task InstallAsync_InvalidName_NeverContactsTheMarketplace(string name)
    {
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, [], signature: null);

        var status = await CreateInstaller(handler).InstallAsync(name, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.InvalidName);
        handler.Calls.ShouldBe(0);
    }

    [Test]
    public async Task InstallAsync_PluginServiceRefusesInstall_ReportsInstallFailed()
    {
        _featurePluginService.InstallAsync(PluginName).Returns(false);
        var zip = BuildZip(Manifest(PluginName));
        var handler = new MarketplaceStubHandler(HttpStatusCode.OK, zip, SignPlugin(PluginName, zip));

        var status = await CreateInstaller(handler).InstallAsync(PluginName, CancellationToken.None);

        status.ShouldBe(MarketplacePluginInstallStatus.InstallFailed);
    }

    private string SignPlugin(string name, byte[] zip) => _key.SignBound(MarketplaceArtifactTypes.Plugin, name, zip);

    private MarketplacePluginInstaller CreateInstaller(HttpMessageHandler handler)
    {
        var downloader = new MarketplacePackageDownloader(
            _key.CreateTrustingVerifier(),
            NullLogger<MarketplacePackageDownloader>.Instance);
        var client = new MarketplaceClient(new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) }, downloader);

        return new MarketplacePluginInstaller(
            client,
            _featurePluginService,
            NullLogger<MarketplacePluginInstaller>.Instance,
            _pluginRoot);
    }

    private void AssertNothingInstalled()
    {
        Directory.Exists(Path.Combine(_pluginRoot, PluginName)).ShouldBeFalse();
        AssertNoWorkDirectoriesLeft();
        _featurePluginService.DidNotReceive().InstallAsync(Arg.Any<string>());
    }

    private void AssertNoWorkDirectoriesLeft()
    {
        Directory.GetDirectories(_workRoot).Select(Path.GetFileName).ShouldBe(new[] { "Features" });
    }

    private static (string Name, byte[] Content) Manifest(string pluginName) =>
        (ManifestEntry, Encoding.UTF8.GetBytes($$"""{ "name": "{{pluginName}}", "version": "1.0.0" }"""));

    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        return buffer.ToArray();
    }
}
