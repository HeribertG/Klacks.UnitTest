// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the marketplace region-package update cycle: no-op without a recorded package
/// identity, upToDate/updated/updateAvailableAutoOff/blockedByMinVersion/packageNotFound/conflict/
/// error status transitions, strict semantic-version parsing of installed, marketplace and
/// minKlacksVersion strings, the guarantee that a failed import never advances
/// REGION_PACKAGE_VERSION, that ACTIVE_INDUSTRIES is never written by the auto-update, and that a
/// download rejected by the signature verifier is reported as signatureRejected and never applied —
/// including an unsigned download when a vendor public key is configured (signature-stripping
/// downgrade), independent of the RequireSignedRegionPackages flag.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.DTOs.Setup;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionPackageUpdateRunnerTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => FixedUtcNow;
    }

    private ISettingsRepository _settingsRepository = null!;
    private IRegionPackageMarketplaceClient _marketplaceClient = null!;
    private IRegionPackageSignatureVerifier _signatureVerifier = null!;
    private IRegionEntityImportService _entityImportService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Dictionary<string, string> _settingValues = null!;
    private List<(string Type, string Value)> _upsertedSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _settingValues = new Dictionary<string, string>();
        _upsertedSettings = new List<(string Type, string Value)>();

        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsRepository
            .GetSettingNoTracking(Arg.Any<string>())
            .Returns(callInfo =>
            {
                var type = callInfo.Arg<string>();
                return _settingValues.TryGetValue(type, out var value)
                    ? new SettingsModel { Id = Guid.NewGuid(), Type = type, Value = value }
                    : null;
            });
        _settingsRepository
            .UpsertSettingAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                var type = callInfo.ArgAt<string>(0);
                var value = callInfo.ArgAt<string>(1);
                _settingValues[type] = value;
                _upsertedSettings.Add((type, value));
                return Task.CompletedTask;
            });

        _marketplaceClient = Substitute.For<IRegionPackageMarketplaceClient>();
        _signatureVerifier = Substitute.For<IRegionPackageSignatureVerifier>();
        _signatureVerifier
            .Verify(Arg.Any<byte[]>(), Arg.Any<string?>())
            .Returns(RegionPackageSignatureVerification.Ok());
        _entityImportService = Substitute.For<IRegionEntityImportService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private RegionPackageUpdateRunner CreateRunner(IRegionPackageSignatureVerifier? signatureVerifier = null)
    {
        return new RegionPackageUpdateRunner(
            _settingsRepository,
            _marketplaceClient,
            signatureVerifier ?? _signatureVerifier,
            _entityImportService,
            _unitOfWork,
            new FixedTimeProvider(),
            NullLogger<RegionPackageUpdateRunner>.Instance);
    }

    private static MarketplaceRegionPackageDownload Download(string profileJson, string? signature = null)
    {
        return new MarketplaceRegionPackageDownload(Encoding.UTF8.GetBytes(profileJson), profileJson, signature);
    }

    private void StubInstalledPackage(string country = "ch", string version = "1.0.0")
    {
        _settingValues[SettingKeys.RegionPackageCountry] = country;
        _settingValues[SettingKeys.RegionPackageVersion] = version;
    }

    private void StubLatest(string version, string minKlacksVersion = "1.0.0")
    {
        _marketplaceClient
            .GetLatestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MarketplaceRegionPackageLookup.Found(
                new MarketplaceRegionPackageInfo { Country = "ch", Version = version, MinKlacksVersion = minKlacksVersion }));
    }

    private RegionPackageUpdateStatus GetWrittenStatus()
    {
        _settingValues.ShouldContainKey(SettingKeys.RegionPackageUpdateStatus);
        var status = JsonSerializer.Deserialize<RegionPackageUpdateStatus>(
            _settingValues[SettingKeys.RegionPackageUpdateStatus],
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        status.ShouldNotBeNull();
        return status!;
    }

    [Test]
    public async Task RunCycleAsync_NoPackageIdentity_DoesNothing()
    {
        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _marketplaceClient.DidNotReceiveWithAnyArgs().GetLatestAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _upsertedSettings.ShouldBeEmpty();
    }

    [Test]
    public async Task RunCycleAsync_SameVersion_WritesUpToDateWithoutImport()
    {
        StubInstalledPackage(version: "1.2.0");
        StubLatest("1.2.0");

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.UpToDate);
        status.InstalledVersion.ShouldBe("1.2.0");
        status.AvailableVersion.ShouldBe("1.2.0");
        status.LastError.ShouldBeNull();
        status.LastCheckUtc.ShouldBe(FixedUtcNow.UtcDateTime);
        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
    }

    [Test]
    public async Task RunCycleAsync_NewerVersionAutoOn_ImportsAndAdvancesVersion()
    {
        StubInstalledPackage(version: "1.0.9");
        StubLatest("1.0.10");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns(Download("""{ "version": 1 }"""));

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _entityImportService.Received(1).ApplyEntityImportsAsync(Arg.Any<RegionSetupProfile>());
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.10");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Updated);
        status.InstalledVersion.ShouldBe("1.0.10");
        status.AvailableVersion.ShouldBe("1.0.10");
        status.LastError.ShouldBeNull();
    }

    [Test]
    public async Task RunCycleAsync_MinKlacksVersionTooHigh_BlocksWithoutImport()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("2.0.0", minKlacksVersion: "999.0.0");

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.BlockedByMinVersion);
        status.LastError.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task RunCycleAsync_AutoUpdateOff_ReportsAvailableWithoutImport()
    {
        StubInstalledPackage(version: "1.0.0");
        _settingValues[SettingKeys.RegionPackageAutoUpdate] = "false";
        StubLatest("1.1.0");

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.UpdateAvailableAutoOff);
        status.AvailableVersion.ShouldBe("1.1.0");
    }

    [Test]
    public async Task RunCycleAsync_ImportThrowsInvalidRequest_ReportsConflictAndKeepsVersion()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns(Download("""{ "version": 1 }"""));
        _entityImportService
            .ApplyEntityImportsAsync(Arg.Any<RegionSetupProfile>())
            .Returns<Task>(_ => throw new InvalidRequestException("customer macro 'X' currently carries the standard function"));

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Conflict);
        status.InstalledVersion.ShouldBe("1.0.0");
        status.LastError.ShouldContain("customer macro");
    }

    [Test]
    public async Task RunCycleAsync_ImportThrowsUnexpected_ReportsErrorAndKeepsVersion()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns(Download("""{ "version": 1 }"""));
        _entityImportService
            .ApplyEntityImportsAsync(Arg.Any<RegionSetupProfile>())
            .Returns<Task>(_ => throw new InvalidOperationException("database gone"));

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Error);
        status.LastError.ShouldContain("database gone");
    }

    [Test]
    public async Task RunCycleAsync_MarketplaceLookupFails_ReportsError()
    {
        StubInstalledPackage();
        _marketplaceClient
            .GetLatestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MarketplaceRegionPackageLookup.Failed());

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Error);
        status.AvailableVersion.ShouldBeNull();
        status.LastError.ShouldNotBeNullOrWhiteSpace();
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
    }

    [Test]
    public async Task RunCycleAsync_MarketplaceHasNoPublishedPackage_ReportsPackageNotFoundWithoutError()
    {
        StubInstalledPackage(version: "1.0.0");
        _marketplaceClient
            .GetLatestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MarketplaceRegionPackageLookup.PackageNotFound());

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.PackageNotFound);
        status.AvailableVersion.ShouldBeNull();
        status.LastError.ShouldBeNull();
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        _upsertedSettings.Select(s => s.Type).ShouldBe(new[] { SettingKeys.RegionPackageUpdateStatus });
        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
    }

    [Test]
    public async Task RunCycleAsync_UnparsableMinKlacksVersion_ReportsErrorWithoutImport()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0", minKlacksVersion: "not-a-version");

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Error);
        status.LastError.ShouldContain("not-a-version");
    }

    [Test]
    public async Task RunCycleAsync_UnparsableMarketplaceVersion_ReportsErrorWithoutImport()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("2.x.0");

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Error);
        status.LastError.ShouldContain("2.x.0");
    }

    [Test]
    public async Task RunCycleAsync_UnparsableInstalledVersion_ReportsErrorWithoutImport()
    {
        StubInstalledPackage(version: "broken");
        StubLatest("1.1.0");

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _marketplaceClient.DidNotReceiveWithAnyArgs().DownloadProfileAsync(default!, default);
        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("broken");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.Error);
        status.LastError.ShouldContain("broken");
    }

    [Test]
    public async Task RunCycleAsync_DownloadedProfileWithUnknownField_ReportsErrorWithoutImport()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns(Download("""{ "version": 1, "unknownField": true }"""));

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.Error);
    }

    [Test]
    public async Task RunCycleAsync_DownloadFails_ReportsError()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns((MarketplaceRegionPackageDownload?)null);

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.Error);
    }

    [Test]
    public async Task RunCycleAsync_NeverWritesActiveIndustries()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns(Download("""{ "version": 1 }"""));

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        _upsertedSettings.ShouldNotContain(s => s.Type == SettingKeys.ActiveIndustries);
        _upsertedSettings.Select(s => s.Type).ShouldBe(
            new[] { SettingKeys.RegionPackageVersion, SettingKeys.RegionPackageUpdateStatus },
            ignoreOrder: true);
    }

    [Test]
    public async Task RunCycleAsync_VerifierRejects_ReportsSignatureRejectedAndKeepsVersion()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        _marketplaceClient.DownloadProfileAsync("ch", Arg.Any<CancellationToken>()).Returns(Download("""{ "version": 1 }"""));
        _signatureVerifier
            .Verify(Arg.Any<byte[]>(), Arg.Any<string?>())
            .Returns(RegionPackageSignatureVerification.Rejected("signature does not match"));

        await CreateRunner().RunCycleAsync(CancellationToken.None);

        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        var status = GetWrittenStatus();
        status.LastResult.ShouldBe(RegionPackageUpdateResults.SignatureRejected);
        status.AvailableVersion.ShouldBe("1.1.0");
        status.LastError.ShouldContain("signature does not match");
    }

    private static (RegionPackageSignatureVerifier Verifier, RSA Rsa) CreateRealVerifier(bool requireSigned, bool withPublicKey = true)
    {
        var rsa = RSA.Create(2048);
        var options = new UpdateTrustOptions
        {
            SignaturePublicKey = withPublicKey ? rsa.ExportSubjectPublicKeyInfoPem() : string.Empty,
            RequireSignedRegionPackages = requireSigned
        };

        var verifier = new RegionPackageSignatureVerifier(
            Options.Create(options),
            NullLogger<RegionPackageSignatureVerifier>.Instance);
        return (verifier, rsa);
    }

    private static string SignWith(RSA rsa, string profileJson)
    {
        var payload = Encoding.UTF8.GetBytes(profileJson);
        return Convert.ToBase64String(rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Test]
    public async Task RunCycleAsync_ValidVendorSignature_ImportsAndAdvancesVersion()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        var (verifier, rsa) = CreateRealVerifier(requireSigned: true);
        using var _ = rsa;
        const string profileJson = """{ "version": 1 }""";
        _marketplaceClient
            .DownloadProfileAsync("ch", Arg.Any<CancellationToken>())
            .Returns(Download(profileJson, SignWith(rsa, profileJson)));

        await CreateRunner(verifier).RunCycleAsync(CancellationToken.None);

        await _entityImportService.Received(1).ApplyEntityImportsAsync(Arg.Any<RegionSetupProfile>());
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.1.0");
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.Updated);
    }

    [Test]
    public async Task RunCycleAsync_TamperedPayload_RejectsWithSignatureRejectedStatus()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        var (verifier, rsa) = CreateRealVerifier(requireSigned: false);
        using var _ = rsa;
        const string signedJson = """{ "version": 1 }""";
        const string tamperedJson = """{ "version": 1, "injected": true }""";
        _marketplaceClient
            .DownloadProfileAsync("ch", Arg.Any<CancellationToken>())
            .Returns(Download(tamperedJson, SignWith(rsa, signedJson)));

        await CreateRunner(verifier).RunCycleAsync(CancellationToken.None);

        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.SignatureRejected);
    }

    [Test]
    public async Task RunCycleAsync_MissingSignature_RequiredSetting_RejectsWithoutImport()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        var (verifier, rsa) = CreateRealVerifier(requireSigned: true);
        using var _ = rsa;
        _marketplaceClient
            .DownloadProfileAsync("ch", Arg.Any<CancellationToken>())
            .Returns(Download("""{ "version": 1 }"""));

        await CreateRunner(verifier).RunCycleAsync(CancellationToken.None);

        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.SignatureRejected);
    }

    [Test]
    public async Task RunCycleAsync_MissingSignature_NotRequiredSetting_KeyConfigured_RejectsWithSignatureRejectedStatus()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        var (verifier, rsa) = CreateRealVerifier(requireSigned: false);
        using var _ = rsa;
        _marketplaceClient
            .DownloadProfileAsync("ch", Arg.Any<CancellationToken>())
            .Returns(Download("""{ "version": 1 }"""));

        await CreateRunner(verifier).RunCycleAsync(CancellationToken.None);

        await _entityImportService.DidNotReceiveWithAnyArgs().ApplyEntityImportsAsync(default!);
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.0.0");
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.SignatureRejected);
    }

    [Test]
    public async Task RunCycleAsync_MissingSignature_NotRequiredSetting_NoKeyConfigured_ImportsAndAdvancesVersion()
    {
        StubInstalledPackage(version: "1.0.0");
        StubLatest("1.1.0");
        var (verifier, rsa) = CreateRealVerifier(requireSigned: false, withPublicKey: false);
        using var _ = rsa;
        _marketplaceClient
            .DownloadProfileAsync("ch", Arg.Any<CancellationToken>())
            .Returns(Download("""{ "version": 1 }"""));

        await CreateRunner(verifier).RunCycleAsync(CancellationToken.None);

        await _entityImportService.Received(1).ApplyEntityImportsAsync(Arg.Any<RegionSetupProfile>());
        _settingValues[SettingKeys.RegionPackageVersion].ShouldBe("1.1.0");
        GetWrittenStatus().LastResult.ShouldBe(RegionPackageUpdateResults.Updated);
    }
}
