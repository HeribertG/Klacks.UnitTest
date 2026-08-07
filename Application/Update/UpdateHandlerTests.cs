// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Update;

using Klacks.Api.Application.Commands.Update;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Handlers.Update;
using Klacks.Api.Application.Queries.Update;
using Klacks.Api.Application.Services.Update;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Update;
using Klacks.Api.Domain.Models.Update;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class UpdateHandlerTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private IUpdateHistoryRepository _repository = null!;
    private IUpdateManifestReader _manifestReader = null!;
    private ISettingsReader _settingsReader = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IUpdateHistoryRepository>();
        _manifestReader = Substitute.For<IUpdateManifestReader>();
        _settingsReader = Substitute.For<ISettingsReader>();
    }

    private GetUpdateStatusQueryHandler CreateStatusHandler()
    {
        return new GetUpdateStatusQueryHandler(_repository, _manifestReader, new UpdateAvailabilityEvaluator(), _settingsReader);
    }

    private TriggerUpdateCommandHandler CreateTriggerHandler()
    {
        return new TriggerUpdateCommandHandler(_repository, _manifestReader, new UpdateAvailabilityEvaluator(), _settingsReader);
    }

    private DeleteUpdateHistoryCommandHandler CreateDeleteHandler()
    {
        return new DeleteUpdateHistoryCommandHandler(_repository);
    }

    private void GivenManifest(string latest, string minUpgradableFrom)
    {
        _manifestReader.GetManifestAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateManifest { Channel = UpdateChannel.Stable, LatestVersion = latest, MinUpgradableFrom = minUpgradableFrom });
    }

    private static UpdateHistory Entry(UpdateOperationStatus status, UpdateOperationType type = UpdateOperationType.Update)
    {
        return new UpdateHistory
        {
            Id = Guid.NewGuid(),
            OperationType = type,
            Status = status,
            Channel = UpdateChannel.Stable,
            FromVersion = "1.0.0",
            TargetVersion = "1.1.0",
            RequestedAt = DateTime.UtcNow,
        };
    }

    [Test]
    public async Task Status_maps_active_and_last_operations()
    {
        var active = Entry(UpdateOperationStatus.Running);
        var last = Entry(UpdateOperationStatus.Succeeded);
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns(active);
        _repository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>()).Returns(last);

        var handler = CreateStatusHandler();
        var result = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        result.CurrentVersion.ShouldNotBeNullOrEmpty();
        result.ActiveOperation.ShouldNotBeNull();
        result.ActiveOperation!.Status.ShouldBe(nameof(UpdateOperationStatus.Running));
        result.LastOperation.ShouldNotBeNull();
        result.LastOperation!.Status.ShouldBe(nameof(UpdateOperationStatus.Succeeded));
    }

    [Test]
    public async Task Status_returns_nulls_when_no_operations()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _repository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var handler = CreateStatusHandler();
        var result = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        result.ActiveOperation.ShouldBeNull();
        result.LastOperation.ShouldBeNull();
    }

    [Test]
    public async Task Status_hides_third_party_active_operation()
    {
        var whisperActive = Entry(UpdateOperationStatus.Running, UpdateOperationType.WhisperInstall);
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns(whisperActive);
        _repository.GetLatestByTypesAsync(Arg.Any<IReadOnlyCollection<UpdateOperationType>>(), Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var handler = CreateStatusHandler();
        var result = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        result.ActiveOperation.ShouldBeNull();
    }

    [Test]
    public async Task Status_last_operation_only_considers_klacks_self_update_types()
    {
        var klacksUpdate = Entry(UpdateOperationStatus.Succeeded, UpdateOperationType.Update);
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _repository.GetLatestByTypesAsync(
            Arg.Is<IReadOnlyCollection<UpdateOperationType>>(types =>
                types.Contains(UpdateOperationType.Update) && types.Contains(UpdateOperationType.Rollback)
                && !types.Contains(UpdateOperationType.WhisperInstall) && !types.Contains(UpdateOperationType.WhisperUninstall)),
            Arg.Any<CancellationToken>()).Returns(klacksUpdate);

        var handler = CreateStatusHandler();
        var result = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        result.LastOperation.ShouldNotBeNull();
        result.LastOperation!.OperationType.ShouldBe(nameof(UpdateOperationType.Update));
    }

    [Test]
    public async Task Status_reports_availability_from_manifest()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _repository.GetRecentAsync(1, Arg.Any<CancellationToken>()).Returns(new List<UpdateHistory>());
        _manifestReader.GetManifestAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateManifest { Channel = UpdateChannel.Stable, LatestVersion = "9.9.9", MinUpgradableFrom = "1.0.0" });

        var handler = CreateStatusHandler();
        var result = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        result.Availability.ShouldNotBeNull();
        result.Availability!.IsUpdateAvailable.ShouldBeTrue();
        result.Availability.LatestVersion.ShouldBe("9.9.9");
    }

    [Test]
    public async Task Status_availability_null_when_no_manifest()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _repository.GetRecentAsync(1, Arg.Any<CancellationToken>()).Returns(new List<UpdateHistory>());
        _manifestReader.GetManifestAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>()).Returns((UpdateManifest?)null);

        var handler = CreateStatusHandler();
        var result = await handler.Handle(new GetUpdateStatusQuery(), CancellationToken.None);

        result.Availability.ShouldBeNull();
    }

    [Test]
    public async Task History_clamps_take_and_maps_entries()
    {
        _repository.GetRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<UpdateHistory> { Entry(UpdateOperationStatus.Succeeded) });

        var handler = new GetUpdateHistoryQueryHandler(_repository);
        var result = await handler.Handle(new GetUpdateHistoryQuery(99999), CancellationToken.None);

        await _repository.Received(1).GetRecentAsync(100, Arg.Any<CancellationToken>());
        result.Count.ShouldBe(1);
    }

    [Test]
    public async Task Cancel_pending_operation_succeeds()
    {
        var entry = Entry(UpdateOperationStatus.Pending);
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var handler = new CancelUpdateCommandHandler(_repository);
        var result = await handler.Handle(new CancelUpdateCommand(entry.Id), CancellationToken.None);

        result.ShouldBeTrue();
        entry.Status.ShouldBe(UpdateOperationStatus.Cancelled);
        entry.CompletedAt.ShouldNotBeNull();
        await _repository.Received(1).UpdateAsync(entry, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_running_operation_is_rejected()
    {
        var entry = Entry(UpdateOperationStatus.Running);
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var handler = new CancelUpdateCommandHandler(_repository);
        var result = await handler.Handle(new CancelUpdateCommand(entry.Id), CancellationToken.None);

        result.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_missing_operation_returns_false()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var handler = new CancelUpdateCommandHandler(_repository);
        var result = await handler.Handle(new CancelUpdateCommand(Guid.NewGuid()), CancellationToken.None);

        result.ShouldBeFalse();
    }

    [TestCase(UpdateOperationStatus.Failed)]
    [TestCase(UpdateOperationStatus.Succeeded)]
    [TestCase(UpdateOperationStatus.Pending)]
    public async Task Delete_operation_the_updater_cannot_be_executing_succeeds(UpdateOperationStatus status)
    {
        var entry = Entry(status);
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await CreateDeleteHandler().Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None);

        result.ShouldBe(UpdateHistoryDeletionOutcome.Deleted);
        await _repository.Received(1).DeleteAsync(entry, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_running_operation_with_live_lease_is_blocked()
    {
        var entry = Entry(UpdateOperationStatus.Running);
        entry.LastHeartbeatAt = DateTime.UtcNow;
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await CreateDeleteHandler().Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None);

        result.ShouldBe(UpdateHistoryDeletionOutcome.BlockedByLiveOperation);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [TestCase(UpdateOperationType.WhisperInstall)]
    [TestCase(UpdateOperationType.WhisperUninstall)]
    public async Task Delete_running_whisper_operation_with_live_lease_is_blocked(UpdateOperationType type)
    {
        var entry = Entry(UpdateOperationStatus.Running, type);
        entry.LastHeartbeatAt = DateTime.UtcNow;
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await CreateDeleteHandler().Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None);

        result.ShouldBe(UpdateHistoryDeletionOutcome.BlockedByLiveOperation);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_running_operation_with_expired_lease_succeeds()
    {
        var entry = Entry(UpdateOperationStatus.Running);
        entry.LastHeartbeatAt = DateTime.UtcNow - UpdateOperationLease.StuckTimeout - OneMinute;
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await CreateDeleteHandler().Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None);

        result.ShouldBe(UpdateHistoryDeletionOutcome.Deleted);
        await _repository.Received(1).DeleteAsync(entry, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_running_operation_without_heartbeat_succeeds()
    {
        var entry = Entry(UpdateOperationStatus.Running);
        entry.LastHeartbeatAt = null;
        _repository.GetByIdAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await CreateDeleteHandler().Handle(new DeleteUpdateHistoryCommand(entry.Id), CancellationToken.None);

        result.ShouldBe(UpdateHistoryDeletionOutcome.Deleted);
        await _repository.Received(1).DeleteAsync(entry, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_missing_operation_returns_not_found()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var result = await CreateDeleteHandler().Handle(new DeleteUpdateHistoryCommand(Guid.NewGuid()), CancellationToken.None);

        result.ShouldBe(UpdateHistoryDeletionOutcome.NotFound);
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Trigger_enqueues_pending_update_when_available()
    {
        GivenManifest("9.9.9", "1.0.0");
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var result = await CreateTriggerHandler().Handle(new TriggerUpdateCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        result.OperationId.ShouldNotBeNull();
        await _repository.Received(1).AddAsync(
            Arg.Is<UpdateHistory>(e => e.Status == UpdateOperationStatus.Pending
                && e.OperationType == UpdateOperationType.Update
                && e.TargetVersion == "9.9.9"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Trigger_rejected_when_up_to_date()
    {
        GivenManifest("1.0.0", "1.0.0");
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var result = await CreateTriggerHandler().Handle(new TriggerUpdateCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Trigger_rejected_when_operation_active()
    {
        GivenManifest("9.9.9", "1.0.0");
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Entry(UpdateOperationStatus.Running));

        var result = await CreateTriggerHandler().Handle(new TriggerUpdateCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Trigger_rejected_when_intermediate_required()
    {
        GivenManifest("9.9.9", "9.0.0");
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var result = await CreateTriggerHandler().Handle(new TriggerUpdateCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Rollback_enqueues_when_successful_update_exists()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        var lastUpdate = Entry(UpdateOperationStatus.Succeeded);
        lastUpdate.FromVersion = "1.0.0";
        lastUpdate.TargetVersion = "1.1.0";
        lastUpdate.BackupRef = "backup-ref-1";
        _repository.GetLastSuccessfulUpdateAsync(Arg.Any<CancellationToken>()).Returns(lastUpdate);

        var result = await new RequestRollbackCommandHandler(_repository).Handle(new RequestRollbackCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<UpdateHistory>(e => e.OperationType == UpdateOperationType.Rollback
                && e.TargetVersion == "1.0.0"
                && e.RelatedOperationId == lastUpdate.Id
                && e.BackupRef == "backup-ref-1"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Rollback_rejected_when_nothing_to_roll_back()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);
        _repository.GetLastSuccessfulUpdateAsync(Arg.Any<CancellationToken>()).Returns((UpdateHistory?)null);

        var result = await new RequestRollbackCommandHandler(_repository).Handle(new RequestRollbackCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Rollback_rejected_when_operation_active()
    {
        _repository.GetActiveOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Entry(UpdateOperationStatus.Pending));

        var result = await new RequestRollbackCommandHandler(_repository).Handle(new RequestRollbackCommand("admin"), CancellationToken.None);

        result.Enqueued.ShouldBeFalse();
        await _repository.DidNotReceive().AddAsync(Arg.Any<UpdateHistory>(), Arg.Any<CancellationToken>());
    }
}
