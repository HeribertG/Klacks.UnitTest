// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Application.Update;

using System.Globalization;
using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.Handlers.Update;
using Klacks.Api.Application.Queries.Update;
using Klacks.Api.Application.Services.Update;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class EngineUpgradeTests
{
    private const string AdminId = "4f1c2b3a-0000-4000-8000-000000000001";
    private const string EngineImage = "ghcr.io/heribertg/klacks-postgres:19";
    private const string EngineDigest = "sha256:981ea51c18b99f66b59f91b48b5ee32006c494ed34dc973482f40904e48f42b2";
    private const string EngineSignature = "I0XsF5o002DnxYmjw3RGDZx+faGrVp3EcnGTvAlAqThIwEgo==";
    private const string BundleUrl = "https://github.com/HeribertG/Klacks.Api/releases/download/postgres-engine-19.0-pgvector0.8.7/klacks-postgres-19.0-win-x64.zip";
    private const string BundleHash = "8c6038cf697f804008c81614aed4e3fcd375ec418888466bcd74c7e01bb11f6c";
    private const string BundleSignature = "JiRnJEQYtf73Eul4P3CoDMKnnsw4ZCtD==";

    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly InstallationIdentity Docker = new(InstallationKind.Docker, "linux-x64");
    private static readonly InstallationIdentity WindowsX64 = new(InstallationKind.Native, "win-x64");

    private IUpdateHistoryRepository _repository = null!;
    private IUpdateManifestReader _manifestReader = null!;
    private ISettingsReader _settingsReader = null!;
    private IInstallationIdentityProvider _installation = null!;
    private IDatabaseEngineVersionReader _engineVersionReader = null!;
    private SettableTimeProvider _timeProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IUpdateHistoryRepository>();
        _manifestReader = Substitute.For<IUpdateManifestReader>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _installation = Substitute.For<IInstallationIdentityProvider>();
        _engineVersionReader = Substitute.For<IDatabaseEngineVersionReader>();
        _timeProvider = new SettableTimeProvider(Now);

        _installation.Get().Returns(Docker);
        _engineVersionReader.GetMajorVersionAsync(Arg.Any<CancellationToken>()).Returns(17);
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _repository.AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<UpdateHistory>());
        GivenManifest(Engine(19));
        GivenCapabilityReport(Now.AddMinutes(-5), "postgres-engine-upgrade/1");
    }

    private EngineUpgradePreflight CreatePreflight() =>
        new(_repository, _manifestReader, _settingsReader, _installation, _engineVersionReader, _timeProvider);

    private TriggerEngineUpgradeCommandHandler CreateHandler() => new(_repository, CreatePreflight(), _timeProvider);

    private static PostgresEngineArtifact Engine(int major) => new()
    {
        Major = major,
        Version = $"{major}.0",
        Pgvector = "0.8.7",
        Docker = new PostgresEngineDockerArtifact { Image = EngineImage, Sha256 = EngineDigest, Signature = EngineSignature },
        Native = [new OnPremArtifact { Rid = "win-x64", BundleUrl = BundleUrl, Sha256 = BundleHash, Signature = BundleSignature }],
    };

    private void GivenManifest(PostgresEngineArtifact? engine)
    {
        _manifestReader.GetManifestAsync(UpdateChannel.Stable, Arg.Any<CancellationToken>())
            .Returns(new UpdateManifest
            {
                Channel = UpdateChannel.Stable,
                LatestVersion = "1.0.79",
                MinUpgradableFrom = "1.0.0",
                Artifacts = new UpdateArtifacts { Postgres = engine },
                Signature = "app-sig",
            });
    }

    private void GivenCapabilityReport(DateTime reportedAtUtc, params string[] capabilities)
    {
        var list = string.Join(",", capabilities.Select(capability => $"\"{capability}\""));
        var value = "{\"updaterVersion\":\"1.0.79\",\"mode\":\"Docker\",\"capabilities\":[" + list + "],\"reportedAtUtc\":\""
            + reportedAtUtc.ToString("O", CultureInfo.InvariantCulture) + "\"}";
        _settingsReader.GetSetting(SettingsConstants.UPDATER_CAPABILITIES)
            .Returns(new SettingsEntity { Type = SettingsConstants.UPDATER_CAPABILITIES, Value = value });
    }

    private async Task<string?> BlockedReasonOfTriggerAsync()
    {
        var result = await CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None);
        result.Enqueued.ShouldBeFalse();
        result.OperationId.ShouldBeNull();
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
        return result.BlockedReason;
    }

    [Test]
    public async Task Docker_HappyPath_WritesTheRowOfTheSupportScript()
    {
        UpdateHistory? written = null;
        _repository.AddAsync(Arg.Do<UpdateHistory>(entry => written = entry), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<UpdateHistory>());

        var result = await CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        result.BlockedReason.ShouldBeNull();
        written.ShouldNotBeNull();
        result.OperationId.ShouldBe(written!.Id);
        written.Id.ShouldNotBe(Guid.Empty);
        written.OperationType.ShouldBe(UpdateOperationType.PostgresUpgrade);
        ((int)written.OperationType).ShouldBe(4);
        written.Status.ShouldBe(UpdateOperationStatus.Pending);
        ((int)written.Status).ShouldBe(0);
        written.Channel.ShouldBe(UpdateChannel.Stable);
        ((int)written.Channel).ShouldBe(0);
        written.FromVersion.ShouldBe("17");
        written.TargetVersion.ShouldBe("19");
        written.ArtifactRef.ShouldBe(EngineImage);
        written.ArtifactSha256.ShouldBe(EngineDigest);
        written.ArtifactSignature.ShouldBe(EngineSignature);
        written.ContainsMigrations.ShouldBeFalse();
        written.RequestedBy.ShouldBe(AdminId);
        written.RequestedAt.ShouldBe(Now);
        written.IsDeleted.ShouldBeFalse();
        written.BackupRef.ShouldBeNull();
        written.RelatedOperationId.ShouldBeNull();
        written.StartedAt.ShouldBeNull();
        written.CompletedAt.ShouldBeNull();
        written.Message.ShouldBeNull();
    }

    [Test]
    public async Task Native_HappyPath_TakesBundleOfOwnRuntimeIdentifier()
    {
        _installation.Get().Returns(WindowsX64);
        UpdateHistory? written = null;
        _repository.AddAsync(Arg.Do<UpdateHistory>(entry => written = entry), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<UpdateHistory>());

        var result = await CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        written!.ArtifactRef.ShouldBe(BundleUrl);
        written.ArtifactSha256.ShouldBe(BundleHash);
        written.ArtifactSignature.ShouldBe(BundleSignature);
        written.TargetVersion.ShouldBe("19");
    }

    [Test]
    public async Task SkippingAMajor_IsAllowed()
    {
        _engineVersionReader.GetMajorVersionAsync(Arg.Any<CancellationToken>()).Returns(16);

        var result = await CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<UpdateHistory>(entry => entry.FromVersion == "16" && entry.TargetVersion == "19"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoManifest_IsBlocked()
    {
        _manifestReader.GetManifestAsync(UpdateChannel.Stable, Arg.Any<CancellationToken>()).Returns((UpdateManifest?)null);

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.NoEngineInManifest));
    }

    [Test]
    public async Task ManifestWithoutEngine_IsBlocked()
    {
        GivenManifest(null);

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.NoEngineInManifest));
    }

    [Test]
    public async Task NoPackageForThisPlatform_IsBlocked()
    {
        _installation.Get().Returns(new InstallationIdentity(InstallationKind.Native, "win-arm64"));

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.NoArtifactForInstallation));
    }

    [TestCase(19)]
    [TestCase(20)]
    public async Task TargetNotNewerThanCurrent_IsBlocked(int currentMajor)
    {
        _engineVersionReader.GetMajorVersionAsync(Arg.Any<CancellationToken>()).Returns(currentMajor);

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.NotNewerThanCurrent));
    }

    [Test]
    public async Task ActiveOperation_IsBlockedBeforeTheCapabilityCheck()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(new UpdateHistory { Id = Guid.NewGuid(), OperationType = UpdateOperationType.WhisperInstall, Status = UpdateOperationStatus.Running });
        GivenCapabilityReport(Now.AddHours(-2), "postgres-engine-upgrade/1");

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.OperationInProgress));
    }

    [Test]
    public async Task NoCapabilityReport_IsBlocked()
    {
        _settingsReader.GetSetting(SettingsConstants.UPDATER_CAPABILITIES).Returns((SettingsEntity?)null);

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.UpdaterCapabilityMissing));
    }

    [Test]
    public async Task UpdaterWithoutCapability_IsBlocked()
    {
        GivenCapabilityReport(Now.AddMinutes(-1));

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.UpdaterCapabilityMissing));
    }

    [Test]
    public async Task StaleCapabilityReport_IsBlocked()
    {
        GivenCapabilityReport(Now.AddMinutes(-21), "postgres-engine-upgrade/1");

        (await BlockedReasonOfTriggerAsync()).ShouldBe(nameof(EngineUpgradeBlockedReason.UpdaterCapabilityStale));
    }

    [Test]
    public async Task LostInsertRace_IsAnsweredAsOperationInProgress()
    {
        _repository.AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DatabaseUpdateException("single active index", isDuplicate: true));

        var result = await CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        result.BlockedReason.ShouldBe(nameof(EngineUpgradeBlockedReason.OperationInProgress));
    }

    private static IEnumerable<Exception> OtherPersistenceFailures()
    {
        yield return new DatabaseUpdateException("foreign key", isForeignKeyViolation: true);
        yield return new DatabaseUpdateException("other failure");
        yield return new ConcurrencyException("concurrent write");
    }

    [TestCaseSource(nameof(OtherPersistenceFailures))]
    public async Task OtherPersistenceFailures_Propagate(Exception failure)
    {
        _repository.AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>()).ThrowsAsync(failure);

        var thrown = await Should.ThrowAsync<Exception>(
            () => CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None));

        thrown.ShouldBeSameAs(failure);
    }

    [Test]
    public async Task Status_SurvivesAFailingEngineVersionRead()
    {
        _engineVersionReader.GetMajorVersionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("server version query failed"));
        _repository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns((UpdateHistory?)null);
        var handler = new GetUpdateStatusQueryHandler(
            _repository, _manifestReader, new UpdateAvailabilityEvaluator(), _settingsReader, _installation, CreatePreflight(),
            NullLogger<GetUpdateStatusQueryHandler>.Instance);

        var status = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        status.CurrentMajor.ShouldBe(0);
        status.AvailableMajor.ShouldBeNull();
        status.CanStartEngineUpgrade.ShouldBeFalse();
        status.EngineUpgradeBlockedReason.ShouldBeNull();
        status.CurrentVersion.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task Trigger_PropagatesAFailingEngineVersionRead()
    {
        _engineVersionReader.GetMajorVersionAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("server version query failed"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => CreateHandler().Handle(new TriggerEngineUpgradeCommand(AdminId), CancellationToken.None));
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Status_ReportsEngineFactsFromTheSameManifest()
    {
        _repository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns((UpdateHistory?)null);
        var handler = new GetUpdateStatusQueryHandler(
            _repository, _manifestReader, new UpdateAvailabilityEvaluator(), _settingsReader, _installation, CreatePreflight(),
            NullLogger<GetUpdateStatusQueryHandler>.Instance);

        var status = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        status.CurrentMajor.ShouldBe(17);
        status.AvailableMajor.ShouldBe(19);
        status.CanStartEngineUpgrade.ShouldBeTrue();
        status.EngineUpgradeBlockedReason.ShouldBeNull();
        await _manifestReader.Received(1).GetManifestAsync(UpdateChannel.Stable, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Status_ReportsBlockedReasonWhenStale()
    {
        GivenCapabilityReport(Now.AddMinutes(-30), "postgres-engine-upgrade/1");
        _repository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns((UpdateHistory?)null);
        var handler = new GetUpdateStatusQueryHandler(
            _repository, _manifestReader, new UpdateAvailabilityEvaluator(), _settingsReader, _installation, CreatePreflight(),
            NullLogger<GetUpdateStatusQueryHandler>.Instance);

        var status = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        status.CanStartEngineUpgrade.ShouldBeFalse();
        status.EngineUpgradeBlockedReason.ShouldBe(nameof(EngineUpgradeBlockedReason.UpdaterCapabilityStale));
        status.AvailableMajor.ShouldBe(19);
    }
}
