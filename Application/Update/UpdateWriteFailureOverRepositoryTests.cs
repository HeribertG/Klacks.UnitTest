// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what the update and Whisper hand-off handlers answer when the self-committing UpdateHistoryRepository's write is
/// rejected by the database, over the real repository on EF InMemory with a save that fails like PostgreSQL. The four
/// enqueue handlers (update, rollback, Whisper install/uninstall) reject every failed insert as "operation in progress"
/// (the partial unique index on the active row, a concurrency failure included); cancel and delete answer a concurrency
/// failure (the status token moved underneath) with their "no longer possible" outcome, and let every other write
/// failure escape unchanged as EF Core's DbUpdateException, which the error middleware answers with 400.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Handlers.Update;
using Klacks.Api.Application.Services.Update;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Repositories.Update;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Update;

[TestFixture]
public class UpdateWriteFailureOverRepositoryTests
{
    private const string Requester = "admin";
    private const string LatestVersion = "9.9.9";

    private static IInstallationIdentityProvider DockerInstallation()
    {
        var installation = Substitute.For<IInstallationIdentityProvider>();
        installation.Get().Returns(new InstallationIdentity(InstallationKind.Docker, "linux-x64"));
        return installation;
    }

    private SaveChangesFailingDataBaseContext _context = null!;
    private UpdateHistoryRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new SaveChangesFailingDataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repository = new UpdateHistoryRepository(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static IEnumerable<TestCaseData> InsertFailures()
    {
        yield return new TestCaseData((Func<Exception>)SaveChangesFailingDataBaseContext.UniqueViolation).SetName("UniqueViolation");
        yield return new TestCaseData((Func<Exception>)(() => new DbUpdateConcurrencyException("concurrency"))).SetName("ConcurrencyFailure");
    }

    [TestCaseSource(nameof(InsertFailures))]
    public async Task TriggerUpdate_FailedInsert_IsRejectedAsOperationInProgress(Func<Exception> failure)
    {
        _context.FailNextSave = failure;

        var result = await CreateTriggerHandler().Handle(new TriggerUpdateCommand(Requester), CancellationToken.None);

        ShouldBeRejectedAsInProgress(result);
    }

    [TestCaseSource(nameof(InsertFailures))]
    public async Task RequestRollback_FailedInsert_IsRejectedAsOperationInProgress(Func<Exception> failure)
    {
        await SeedAsync(Entry(UpdateOperationType.Update, UpdateOperationStatus.Succeeded));
        _context.FailNextSave = failure;

        var result = await new RequestRollbackCommandHandler(_repository)
            .Handle(new RequestRollbackCommand(Requester), CancellationToken.None);

        ShouldBeRejectedAsInProgress(result);
    }

    [TestCaseSource(nameof(InsertFailures))]
    public async Task InstallWhisper_FailedInsert_IsRejectedAsOperationInProgress(Func<Exception> failure)
    {
        _context.FailNextSave = failure;

        var result = await new InstallWhisperPluginCommandHandler(_repository, DockerInstallation()).Handle(
            new InstallWhisperPluginCommand(WhisperPluginConstants.ModelAliasLarge, Requester), CancellationToken.None);

        ShouldBeRejectedAsInProgress(result);
    }

    [TestCaseSource(nameof(InsertFailures))]
    public async Task UninstallWhisper_FailedInsert_IsRejectedAsOperationInProgress(Func<Exception> failure)
    {
        var providers = Substitute.For<ICustomSttProviderRepository>();
        providers.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>()).Returns(new CustomSttProvider
        {
            Id = WhisperPluginConstants.ProviderId,
            Name = WhisperPluginConstants.ProviderName,
            ApiUrl = WhisperPluginConstants.ProviderApiUrl,
            LanguageModel = WhisperPluginConstants.LargeModelId,
            IsEnabled = true,
            IsSystem = true,
        });
        _context.FailNextSave = failure;

        var result = await new UninstallWhisperPluginCommandHandler(
                _repository, providers, Substitute.For<ISettingsRepository>(), Substitute.For<IUnitOfWork>())
            .Handle(new UninstallWhisperPluginCommand(Requester), CancellationToken.None);

        ShouldBeRejectedAsInProgress(result);
    }

    [Test]
    public async Task Cancel_ConcurrencyFailure_AnswersNoLongerPending()
    {
        var entry = await SeedAsync(Entry(UpdateOperationType.Update, UpdateOperationStatus.Pending));
        _context.FailNextSave = () => new DbUpdateConcurrencyException("status moved");

        var outcome = await new CancelUpdateCommandHandler(_repository).Handle(new CancelUpdateCommand(entry.Id), CancellationToken.None);

        outcome.ShouldBe(UpdateOperationCancellationOutcome.NoLongerPending);
    }

    [Test]
    public async Task Cancel_OtherWriteFailure_EscapesAsEfCoreDbUpdateException()
    {
        var entry = await SeedAsync(Entry(UpdateOperationType.Update, UpdateOperationStatus.Pending));
        _context.FailNextSave = SaveChangesFailingDataBaseContext.UniqueViolation;

        var exception = await Should.ThrowAsync<Exception>(
            () => new CancelUpdateCommandHandler(_repository).Handle(new CancelUpdateCommand(entry.Id), CancellationToken.None));

        exception.ShouldBeOfType<DbUpdateException>();
    }

    [Test]
    public async Task Delete_ConcurrencyFailure_AnswersBlockedByLiveOperation()
    {
        var entry = await SeedAsync(Entry(UpdateOperationType.Update, UpdateOperationStatus.Pending));
        _context.FailNextSave = () => new DbUpdateConcurrencyException("status moved");

        var outcome = await new DeleteUpdateHistoryCommandHandler(_repository)
            .Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None);

        outcome.ShouldBe(UpdateHistoryDeletionOutcome.BlockedByLiveOperation);
    }

    [Test]
    public async Task Delete_OtherWriteFailure_EscapesAsEfCoreDbUpdateException()
    {
        var entry = await SeedAsync(Entry(UpdateOperationType.Update, UpdateOperationStatus.Failed));
        _context.FailNextSave = SaveChangesFailingDataBaseContext.UniqueViolation;

        var exception = await Should.ThrowAsync<Exception>(
            () => new DeleteUpdateHistoryCommandHandler(_repository).Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None));

        exception.ShouldBeOfType<DbUpdateException>();
    }

    private static void ShouldBeRejectedAsInProgress(UpdateTriggerResult result)
    {
        result.Enqueued.ShouldBeFalse();
        result.Reason.ShouldBe(UpdateReasons.OperationInProgress);
    }

    private async Task<UpdateHistory> SeedAsync(UpdateHistory entry)
    {
        _context.UpdateHistory.Add(entry);
        await _context.SaveChangesAsync();
        return entry;
    }

    private static UpdateHistory Entry(UpdateOperationType type, UpdateOperationStatus status) => new()
    {
        Id = Guid.NewGuid(),
        OperationType = type,
        Status = status,
        Channel = UpdateChannel.Stable,
        FromVersion = "1.0.0",
        TargetVersion = "1.1.0",
        RequestedBy = Requester,
        RequestedAt = DateTime.UtcNow,
        CompletedAt = status == UpdateOperationStatus.Succeeded ? DateTime.UtcNow : null,
    };

    private TriggerUpdateCommandHandler CreateTriggerHandler()
    {
        var manifestReader = Substitute.For<IUpdateManifestReader>();
        manifestReader.GetManifestAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>()).Returns(new UpdateManifest
        {
            Channel = UpdateChannel.Stable,
            LatestVersion = LatestVersion,
            MinUpgradableFrom = "1.0.0",
            Artifacts = new UpdateArtifacts
            {
                Docker = new DockerArtifact { ApiImage = "ghcr.io/heribertg/klacks-api:" + LatestVersion, Sha256 = "sha256:abc" }
            },
            Signature = "sig",
        });
        var installation = Substitute.For<IInstallationIdentityProvider>();
        installation.Get().Returns(new InstallationIdentity(InstallationKind.Docker, "linux-x64"));

        return new TriggerUpdateCommandHandler(
            _repository, manifestReader, new UpdateAvailabilityEvaluator(), Substitute.For<ISettingsReader>(), installation);
    }
}
