// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Verifies the local voice handlers: the install handler rejects a missing consent, a wrong licence version and an
/// active operation without writing anything, persists the consent (user, UTC time, version) before it queues the
/// TtsInstall row, and maps a rejected insert to a conflict; the status handler derives installed, pending, last error
/// and licence state from the settings and the update queue; the licence query returns the embedded text.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;
using Klacks.UnitTest.TestHelpers;
using NSubstitute.ExceptionExtensions;
using SettingKeys = Klacks.Api.Application.Constants.Settings;

namespace Klacks.UnitTest.Application.Assistant;

[TestFixture]
public class LocalTtsHandlerTests
{
    private const string AdminId = "admin-user-id";
    private const string ExpectedLicenseSha256 = "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);

    private ISettingsRepository _settings = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IUpdateHistoryRepository _updates = null!;
    private Dictionary<string, string> _store = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Dictionary<string, string>();
        _settings = Substitute.For<ISettingsRepository>();
        _settings.UpsertSettingAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                _store[call.ArgAt<string>(0)] = call.ArgAt<string>(1);
                return Task.CompletedTask;
            });
        _settings.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(_store));
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _updates = Substitute.For<IUpdateHistoryRepository>();
    }

    private InstallLocalTtsCommandHandler InstallHandler() =>
        new(_settings, _unitOfWork, _updates, new SettableTimeProvider(Now.UtcDateTime), Substitute.For<ILogger<InstallLocalTtsCommandHandler>>());

    private GetLocalTtsStatusQueryHandler StatusHandler() => new(_settings, _updates);

    private static UpdateHistory Operation(UpdateOperationType type, UpdateOperationStatus status, string? message = null) => new()
    {
        Id = Guid.NewGuid(),
        OperationType = type,
        Status = status,
        Message = message,
        RequestedAt = Now.UtcDateTime,
    };

    [Test]
    public async Task Install_WithoutConsent_IsRejectedAndWritesNothing()
    {
        var result = await InstallHandler().Handle(
            new InstallLocalTtsCommand(false, LocalTtsConstants.LicenseVersion, AdminId), CancellationToken.None);

        result.Outcome.ShouldBe(LocalTtsInstallOutcome.InvalidConsent);
        result.Reason.ShouldBe(LocalTtsReasons.LicenseNotAccepted);
        _store.ShouldBeEmpty();
        await _updates.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [TestCase(null)]
    [TestCase("openrail-m-supertonic3-old")]
    public async Task Install_WithOtherLicenseVersion_IsRejectedAndWritesNothing(string? version)
    {
        var result = await InstallHandler().Handle(new InstallLocalTtsCommand(true, version, AdminId), CancellationToken.None);

        result.Outcome.ShouldBe(LocalTtsInstallOutcome.InvalidConsent);
        result.Reason.ShouldBe(LocalTtsReasons.LicenseVersionMismatch);
        _store.ShouldBeEmpty();
        await _updates.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [TestCase(UpdateOperationType.TtsInstall, LocalTtsReasons.InstallAlreadyPending)]
    [TestCase(UpdateOperationType.Update, UpdateReasons.OperationInProgress)]
    public async Task Install_WhileAnOperationIsActive_IsAConflictAndWritesNothing(UpdateOperationType activeType, string reason)
    {
        _updates.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns(Operation(activeType, UpdateOperationStatus.Pending));

        var result = await InstallHandler().Handle(
            new InstallLocalTtsCommand(true, LocalTtsConstants.LicenseVersion, AdminId), CancellationToken.None);

        result.Outcome.ShouldBe(LocalTtsInstallOutcome.Conflict);
        result.Reason.ShouldBe(reason);
        _store.ShouldBeEmpty();
        await _updates.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_WithConsent_PersistsConsentThenQueuesTtsInstall()
    {
        UpdateHistory? queued = null;
        _updates.AddAsync(Arg.Do<UpdateHistory>(h => queued = h), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<UpdateHistory>());

        var result = await InstallHandler().Handle(
            new InstallLocalTtsCommand(true, LocalTtsConstants.LicenseVersion, AdminId), CancellationToken.None);

        result.Outcome.ShouldBe(LocalTtsInstallOutcome.Enqueued);
        result.OperationId.ShouldBe(queued!.Id);
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_BY].ShouldBe(AdminId);
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_AT].ShouldBe("2026-11-21T10:30:00.0000000Z");
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_VERSION].ShouldBe(LocalTtsConstants.LicenseVersion);
        queued.OperationType.ShouldBe(UpdateOperationType.TtsInstall);
        queued.Status.ShouldBe(UpdateOperationStatus.Pending);
        queued.TargetVersion.ShouldBe("supertonic-3");
        queued.ArtifactRef.ShouldBe("supertonic-3-int8");
        queued.RequestedBy.ShouldBe(AdminId);
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _updates.AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Install_WhenTheInsertIsRejected_IsAConflict()
    {
        _updates.AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DatabaseUpdateException("unique"));

        var result = await InstallHandler().Handle(
            new InstallLocalTtsCommand(true, LocalTtsConstants.LicenseVersion, AdminId), CancellationToken.None);

        result.Outcome.ShouldBe(LocalTtsInstallOutcome.Conflict);
        result.Reason.ShouldBe(UpdateReasons.OperationInProgress);
    }

    [Test]
    public async Task Status_Empty_IsNotInstalledAndNotAccepted()
    {
        var status = await StatusHandler().Handle(new GetLocalTtsStatusQuery(), CancellationToken.None);

        status.ShouldBe(new LocalTtsStatus(false, false, null, false, null, 128_774_318));
    }

    [Test]
    public async Task Status_InstalledWithCurrentConsent_ReportsBoth()
    {
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_BASE_URL] = "http://klacks-stt:8000";
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_BY] = AdminId;
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_AT] = "2026-11-21T10:30:00.0000000Z";
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_VERSION] = LocalTtsConstants.LicenseVersion;
        _updates.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>())
            .Returns(Operation(UpdateOperationType.TtsInstall, UpdateOperationStatus.Succeeded, "ok"));

        var status = await StatusHandler().Handle(new GetLocalTtsStatusQuery(), CancellationToken.None);

        status.Installed.ShouldBeTrue();
        status.LicenseAccepted.ShouldBeTrue();
        status.LicenseAcceptedAt.ShouldBe("2026-11-21T10:30:00.0000000Z");
        status.LastError.ShouldBeNull();
    }

    [Test]
    public async Task Status_ConsentToAnOlderLicense_IsNotAccepted()
    {
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_ACCEPTED_BY] = AdminId;
        _store[SettingKeys.ASSISTANT_TTS_LOCAL_LICENSE_VERSION] = "openrail-m-supertonic3-old";

        var status = await StatusHandler().Handle(new GetLocalTtsStatusQuery(), CancellationToken.None);

        status.LicenseAccepted.ShouldBeFalse();
        status.LicenseAcceptedAt.ShouldBeNull();
    }

    [Test]
    public async Task Status_PendingInstallAndLastFailure_AreReported()
    {
        _updates.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Operation(UpdateOperationType.TtsInstall, UpdateOperationStatus.Running));
        _updates.GetLatestByTypesAsync(
                Arg.Is<IReadOnlyCollection<UpdateOperationType>>(t => t.SequenceEqual(new[] { UpdateOperationType.TtsInstall })),
                Arg.Any<CancellationToken>())
            .Returns(Operation(UpdateOperationType.TtsInstall, UpdateOperationStatus.Failed, "download failed"));

        var status = await StatusHandler().Handle(new GetLocalTtsStatusQuery(), CancellationToken.None);

        status.Pending.ShouldBeTrue();
        status.LastError.ShouldBe("download failed");
    }

    [Test]
    public async Task Status_OtherActiveOperation_IsNotPending()
    {
        _updates.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Operation(UpdateOperationType.WhisperInstall, UpdateOperationStatus.Running));

        var status = await StatusHandler().Handle(new GetLocalTtsStatusQuery(), CancellationToken.None);

        status.Pending.ShouldBeFalse();
    }

    [Test]
    public async Task License_ReturnsVersionAndTheEmbeddedOpenRailText()
    {
        var license = await new GetLocalTtsLicenseQueryHandler(new EmbeddedLocalTtsLicenseText())
            .Handle(new GetLocalTtsLicenseQuery(), CancellationToken.None);

        license.Version.ShouldBe("openrail-m-supertonic3-3cadd1ee");
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(license.Text))).ToLowerInvariant()
            .ShouldBe(ExpectedLicenseSha256);
    }

    [Test]
    public void LicenseResource_BytesMatchThePinnedHash()
    {
        using var stream = typeof(EmbeddedLocalTtsLicenseText).Assembly.GetManifestResourceStream(LocalTtsConstants.LicenseResourceName);

        stream.ShouldNotBeNull();
        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant().ShouldBe(ExpectedLicenseSha256);
    }
}
