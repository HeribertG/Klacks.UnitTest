// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the ledger-based ERP import run: ErpOrderImportRunner with the real
/// ErpImportInboxRegistrationService, ErpImportFileImporter, ImportedOrderProcessor and
/// OrderSupersessionService over substituted repositories, storage and parser. Covers the schedule gate,
/// reaping, the retention purge (every drop point, also disabled ones; a failing purge does not stop the import) and
/// registration before any claim, claim order and attempt numbers, every order outcome
/// (Created/Updated/Unchanged/Superseded/Rejected) and its counter, parser rejections counted per rejected order
/// (not per reference), the single verified read of the stored file (hash mismatch, grown, missing and too large
/// objects), one unreadable inbox file or a failing inbox not stopping the import, the
/// Completed/CompletedWithWarnings/Failed decision (all-rejected but parseable is a warning, malformed XML
/// fails), exception linking to the ledger row, heartbeat cadence by order count and by time, abort on a
/// lost heartbeat, cancellation without finish, and an unexpected error finishing the row as Interrupted.
/// </summary>

using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.DTOs.Imports;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpOrderImportRunnerTests
{
    private const string Prefix = "customer-1/";
    private const int ManyOrders = 60;

    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid ProcessGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly ErpDropPoint DropPoint = new()
    {
        Id = Guid.NewGuid(),
        Name = "Test ERP",
        SourceSystemId = "erp-1",
        BucketPrefix = "customer-1",
        IsEnabled = true
    };

    private IErpDropPointRepository _dropPointRepository = null!;
    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IErpImportFileRepository _fileRepository = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IOrderImportParser _parser = null!;
    private IClientRepository _clientRepository = null!;
    private IShiftRepository _shiftRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IAgentTriggerService _triggerService = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SettableTimeProvider _time = null!;
    private ErpImportRunState _runState = null!;
    private ErpImportInstanceIdentity _instance = null!;
    private ErpOrderImportRunner _runner = null!;
    private Client _customer = null!;
    private List<ErpImportFile> _receivedFiles = null!;
    private List<ErpImportFileProgress> _heartbeats = null!;
    private List<FinishCall> _finishes = null!;
    private List<ErpImportException> _exceptions = null!;

    private sealed record FinishCall(Guid FileId, string InstanceId, int Attempt, ErpImportFileStatus Status, ErpImportFileProgress Progress, string? Reason);

    [SetUp]
    public void SetUp()
    {
        _dropPointRepository = Substitute.For<IErpDropPointRepository>();
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _parser = Substitute.For<IOrderImportParser>();
        _clientRepository = Substitute.For<IClientRepository>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _time = new SettableTimeProvider(Now);
        _runState = new ErpImportRunState();
        _instance = new ErpImportInstanceIdentity("test-host", ProcessGuid);
        _customer = new Client { Id = Guid.NewGuid(), Type = EntityTypeEnum.Customer, Company = "Spitex Musterhausen" };
        _receivedFiles = [];
        _heartbeats = [];
        _finishes = [];
        _exceptions = [];

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<Client>>>())
            .Returns(ci => ci.Arg<Func<Task<Client>>>()());

        _settingsRepository.TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new Settings { Type = ErpImportSettingsTypes.NextRunUtc, Value = DateTime.UtcNow.AddMinutes(-5).ToString("O") });

        _dropPointRepository.List().Returns([DropPoint]);
        _objectStorageService.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        _objectStorageService.ListWithMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<StorageObjectMetadata>());
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>()).Returns(_customer);
        _workRepository.GetFutureUnlockedByShiftIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());

        _fileRepository.ListReceivedAsync(DropPoint.Id, Arg.Any<CancellationToken>()).Returns(_ => _receivedFiles.ToList());
        _fileRepository.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _fileRepository.HeartbeatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ErpImportFileProgress>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _heartbeats.Add(ci.ArgAt<ErpImportFileProgress>(3));
                return true;
            });
        _fileRepository.FinishAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ErpImportFileStatus>(), Arg.Any<ErpImportFileProgress>(), Arg.Any<string?>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _finishes.Add(new FinishCall(
                    ci.ArgAt<Guid>(0), ci.ArgAt<string>(1), ci.ArgAt<int>(2), ci.ArgAt<ErpImportFileStatus>(3),
                    ci.ArgAt<ErpImportFileProgress>(4), ci.ArgAt<string?>(5)));
                return true;
            });
        _exceptionRepository.Add(Arg.Do<ErpImportException>(e => _exceptions.Add(e)));

        var registrar = new ErpImportFileRegistrar(_fileRepository, _unitOfWork, _time, NullLogger<ErpImportFileRegistrar>.Instance);
        var inboxRegistration = new ErpImportInboxRegistrationService(
            _objectStorageService, _fileRepository, registrar, _unitOfWork, NullLogger<ErpImportInboxRegistrationService>.Instance);
        var companyClock = new FixedCompanyClock(new DateTimeOffset(Now), TimeZoneInfo.Utc);
        var supersession = new OrderSupersessionService(
            _shiftRepository, _workRepository, _clientRepository, _triggerService, ShiftGroupScopeReaderStub.WithoutAnyGroups(),
            _unitOfWork, companyClock, Substitute.For<IDefaultShiftMacroResolver>(), NullLogger<OrderSupersessionService>.Instance);
        var orderProcessor = new ImportedOrderProcessor(
            new ErpCustomerResolver(_clientRepository), _shiftRepository, supersession, _unitOfWork, Substitute.For<IDefaultShiftMacroResolver>());
        var importer = new ErpImportFileImporter(
            _fileRepository, _objectStorageService, _parser, orderProcessor, _exceptionRepository, _triggerService,
            _unitOfWork, _instance, _time, NullLogger<ErpImportFileImporter>.Instance);

        var legacyFolderMigrator = new ErpImportLegacyFolderMigrator(
            _objectStorageService, _fileRepository, _exceptionRepository, _unitOfWork, _time, NullLogger<ErpImportLegacyFolderMigrator>.Instance);

        _fileRepository.ListExpiredAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<ErpImportFileRetentionCandidate>());
        var retentionService = new ErpImportRetentionService(
            _fileRepository, _exceptionRepository, _objectStorageService, _unitOfWork, _time, NullLogger<ErpImportRetentionService>.Instance);

        _runner = new ErpOrderImportRunner(
            _dropPointRepository, _defaultDropPointProvider, legacyFolderMigrator, _fileRepository, retentionService, inboxRegistration, importer,
            _settingsRepository, companyClock, _unitOfWork, _runState, _time,
            NullLogger<ErpOrderImportRunner>.Instance, new ErpCronTimeZoneDriftNotifier());
    }

    [Test]
    public async Task RunAsync_NotYetDue_SkipsEntirely()
    {
        // Arrange
        GivenNotYetDue();

        // Act
        await _runner.RunAsync();

        // Assert
        await _fileRepository.DidNotReceive().ReapStaleAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().ListReceivedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _defaultDropPointProvider.DidNotReceive().GetOrCreateDefaultAsync(Arg.Any<CancellationToken>());
        _runState.IsRunning.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_FirstEverRun_ComputesScheduleWithoutFiring()
    {
        // Arrange
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc).Returns((Settings?)null);

        // Act
        await _runner.RunAsync();

        // Assert
        await _fileRepository.DidNotReceive().ListReceivedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _settingsRepository.Received(1).AddSetting(Arg.Is<Settings>(s => s.Type == ErpImportSettingsTypes.NextRunUtc));
    }

    [Test]
    public async Task RunAsync_OccurrenceClaimedByAnotherInstance_SkipsEntirely()
    {
        // Arrange
        _settingsRepository.TryAdvanceSettingAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);

        // Act
        await _runner.RunAsync();

        // Assert
        await _settingsRepository.Received(1).TryAdvanceSettingAsync(ErpImportSettingsTypes.NextRunUtc, Arg.Any<string>(), Arg.Any<string>());
        await _fileRepository.DidNotReceive().ListReceivedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_Due_CreatesDefaultDropPointMigratesLegacyFoldersReapsStaleRowsAndRegistersTheInboxBeforeClaiming()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));

        // Act
        await _runner.RunAsync();

        // Assert
        Received.InOrder(() =>
        {
            _defaultDropPointProvider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>());
            _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>());
            _fileRepository.ReapStaleAsync(Now - ErpImportFileTiming.StaleAfter, Arg.Any<CancellationToken>());
            _fileRepository.ListExpiredAsync(DropPoint.Id, Now - ErpImportFileTiming.RetentionPeriod, ErpImportFileTiming.RetentionBatchSize, Arg.Any<CancellationToken>());
            _objectStorageService.ListAsync(Prefix, Arg.Any<CancellationToken>());
            _fileRepository.ListReceivedAsync(DropPoint.Id, Arg.Any<CancellationToken>());
            _fileRepository.TryClaimAsync(file.Id, file.Attempt, _instance.InstanceId, Now, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RunAsync_RetentionFailing_StillRegistersAndImportsTheReceivedFiles()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        _fileRepository.ListExpiredAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database hiccup"));

        // Act
        await _runner.RunAsync();

        // Assert
        await _objectStorageService.Received(1).ListAsync(Prefix, Arg.Any<CancellationToken>());
        _finishes.ShouldHaveSingleItem().FileId.ShouldBe(file.Id);
    }

    [Test]
    public async Task RunAsync_DisabledDropPoint_StillGetsTheRetentionPurge()
    {
        // Arrange
        var disabled = new ErpDropPoint { Id = Guid.NewGuid(), Name = "Disabled", SourceSystemId = "erp-2", BucketPrefix = "customer-2", IsEnabled = false };
        _dropPointRepository.List().Returns([disabled]);

        // Act
        await _runner.RunAsync();

        // Assert
        await _fileRepository.Received(1).ListExpiredAsync(disabled.Id, Arg.Any<DateTime>(), ErpImportFileTiming.RetentionBatchSize, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_LegacyMigrationFailing_StillImportsTheReceivedFiles()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        _objectStorageService.ListWithMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedAccessException("no access"));

        // Act
        await _runner.RunAsync();

        // Assert
        _finishes.ShouldHaveSingleItem().FileId.ShouldBe(file.Id);
    }

    [Test]
    public async Task RunAsync_DisabledDropPoint_IsNeitherRegisteredNorImported()
    {
        // Arrange
        var disabled = new ErpDropPoint { Id = Guid.NewGuid(), Name = "Disabled", SourceSystemId = "erp-2", BucketPrefix = "customer-2", IsEnabled = false };
        _dropPointRepository.List().Returns([disabled]);

        // Act
        await _runner.RunAsync();

        // Assert
        await _objectStorageService.DidNotReceive().ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _fileRepository.DidNotReceive().ListReceivedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _objectStorageService.Received(1).ListWithMetadataAsync("customer-2/", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ReceivedFiles_AreClaimedInListedOrderWithTheirAttemptNumber()
    {
        // Arrange
        var older = GivenReceivedFile(NewOrder("ORD-1"));
        var newer = GivenReceivedFile(NewOrder("ORD-2"));
        newer.Attempt = 2;

        // Act
        await _runner.RunAsync();

        // Assert
        Received.InOrder(() =>
        {
            _fileRepository.TryClaimAsync(older.Id, 0, _instance.InstanceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
            _fileRepository.TryClaimAsync(newer.Id, 2, _instance.InstanceId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        });
        _finishes.Select(f => (f.FileId, f.Attempt)).ShouldBe(new[] { (older.Id, 1), (newer.Id, 3) });
    }

    [Test]
    public async Task RunAsync_ClaimLost_SkipsTheFileWithoutReadingIt()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        _fileRepository.TryClaimAsync(file.Id, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await _runner.RunAsync();

        // Assert
        await _objectStorageService.DidNotReceive().GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        _heartbeats.ShouldBeEmpty();
        _finishes.ShouldBeEmpty();
    }

    [Test]
    public async Task RunAsync_NewOrder_CreatesADraftAndFinishesCompletedAsThisInstanceAndAttempt()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s =>
            s.Status == ShiftStatus.OriginalOrder && s.SourceSystemId == "erp-1" && s.ExternalOrderReference == "ORD-1"));
        var finish = _finishes.ShouldHaveSingleItem();
        finish.FileId.ShouldBe(file.Id);
        finish.InstanceId.ShouldBe(_instance.InstanceId);
        finish.Attempt.ShouldBe(1);
        finish.Status.ShouldBe(ErpImportFileStatus.Completed);
        finish.Progress.ShouldBe(new ErpImportFileProgress(1, 1, 1, 0, 0, 0, 0));
        finish.Reason.ShouldBeNull();
        await _objectStorageService.DidNotReceive().TryMoveAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ChangedDraft_IsUpdated()
    {
        // Arrange
        var order = NewOrder("ORD-1");
        GivenReceivedFile(order);
        var draft = ImportedOrderShiftMapper.BuildDraft(order, _customer.Id);
        draft.SumEmployees = 99;
        GivenActiveShift(draft);

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.Received(1).PutWithSealedOrderHandling(Arg.Is<Shift>(s => s.Id == draft.Id && s.SumEmployees == order.SumEmployees));
        _finishes.ShouldHaveSingleItem().Progress.ShouldBe(new ErpImportFileProgress(1, 1, 0, 1, 0, 0, 0));
    }

    [Test]
    public async Task RunAsync_UnchangedDraft_IsNotWritten()
    {
        // Arrange
        var order = NewOrder("ORD-1");
        GivenReceivedFile(order);
        GivenActiveShift(ImportedOrderShiftMapper.BuildDraft(order, _customer.Id));

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.Completed);
        finish.Progress.ShouldBe(new ErpImportFileProgress(1, 1, 0, 0, 1, 0, 0));
    }

    [Test]
    public async Task RunAsync_UnchangedSealedOrder_CountsAsUnchangedWithoutSupersession()
    {
        // Arrange
        var order = NewOrder("ORD-1");
        GivenReceivedFile(order);
        GivenActiveShift(SealedOrderFor(order));

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        await _shiftRepository.DidNotReceive().PutWithSealedOrderHandling(Arg.Any<Shift>());
        await _triggerService.DidNotReceive().OnEventAsync(Arg.Any<IAgentTriggerEvent>(), Arg.Any<CancellationToken>());
        _finishes.ShouldHaveSingleItem().Progress.ShouldBe(new ErpImportFileProgress(1, 1, 0, 0, 1, 0, 0));
    }

    [Test]
    public async Task RunAsync_ChangedSealedOrder_CountsAsSuperseded()
    {
        // Arrange
        var order = NewOrder("ORD-1");
        GivenReceivedFile(order);
        var sealedOrder = SealedOrderFor(order);
        sealedOrder.EndShift = sealedOrder.EndShift.AddHours(1);
        GivenActiveShift(sealedOrder);

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.SupersedesOrderId == sealedOrder.Id));
        _finishes.ShouldHaveSingleItem().Progress.ShouldBe(new ErpImportFileProgress(1, 1, 0, 0, 0, 1, 0));
    }

    [Test]
    public async Task RunAsync_MixedFile_CountsEveryOutcomeAndFinishesWithWarnings()
    {
        // Arrange
        var created = NewOrder("ORD-1");
        var updated = NewOrder("ORD-2");
        var unchanged = NewOrder("ORD-3");
        var superseded = NewOrder("ORD-4");
        var file = GivenReceivedFile(
            new OrderImportParseResult
            {
                Orders = [created, updated, unchanged, superseded],
                Errors = [new OrderImportValidationError { Field = "Weekdays", Message = "no weekday", ExternalOrderReference = "ORD-5" }]
            });

        var changedDraft = ImportedOrderShiftMapper.BuildDraft(updated, _customer.Id);
        changedDraft.Description = "outdated";
        GivenActiveShift(changedDraft);
        GivenActiveShift(ImportedOrderShiftMapper.BuildDraft(unchanged, _customer.Id));
        var sealedOrder = SealedOrderFor(superseded);
        sealedOrder.Quantity = 5;
        GivenActiveShift(sealedOrder);

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
        finish.Progress.ShouldBe(new ErpImportFileProgress(5, 5, 1, 1, 1, 1, 1));
        finish.Reason.ShouldBeNull();
        var rejected = _exceptions.ShouldHaveSingleItem();
        rejected.ExternalOrderReference.ShouldBe("ORD-5");
        rejected.ImportFileId.ShouldBe(file.Id);
        rejected.FileKey.ShouldBe(file.StorageKey);
        rejected.SourceSystemId.ShouldBe(DropPoint.SourceSystemId);
        rejected.Reason.ShouldContain("no weekday");
    }

    [Test]
    public async Task RunAsync_OrderFailingWhileImporting_IsRecordedAsALinkedExceptionWithAnAdminAlert()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"), NewOrder("ORD-2"));
        _shiftRepository.AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.ExternalOrderReference == "ORD-1"))
            .ThrowsAsync(new InvalidOperationException("db down"));

        // Act
        await _runner.RunAsync();

        // Assert
        var exception = _exceptions.ShouldHaveSingleItem();
        exception.ExternalOrderReference.ShouldBe("ORD-1");
        exception.ImportFileId.ShouldBe(file.Id);
        exception.FileKey.ShouldBe(file.StorageKey);
        exception.Reason.ShouldContain("db down");
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e => e.Kind == AgentTriggerKinds.OrderImportFailed && e.AdminOnly),
            Arg.Any<CancellationToken>());
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
        finish.Progress.ShouldBe(new ErpImportFileProgress(2, 2, 1, 0, 0, 0, 1));
    }

    [Test]
    public async Task RunAsync_OpenDraftUniqueIndexViolation_IsRejectedWithTheConcurrentDraftReason()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .ThrowsAsync(new DatabaseUpdateException(
                "duplicate key value violates unique constraint", isDuplicate: true,
                constraintName: ShiftIndexNames.OpenDraftReferenceUnique));

        // Act
        await _runner.RunAsync();

        // Assert
        var exception = _exceptions.ShouldHaveSingleItem();
        exception.ExternalOrderReference.ShouldBe("ORD-1");
        exception.ImportFileId.ShouldBe(file.Id);
        exception.Reason.ShouldBe(ErpImportFileFailureReasons.ConcurrentOpenDraft);
        _finishes.ShouldHaveSingleItem().Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
    }

    [Test]
    public async Task RunAsync_OtherDuplicateConstraintViolation_KeepsTheDatabaseMessageAsReason()
    {
        // Arrange
        GivenReceivedFile(NewOrder("ORD-1"));
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .ThrowsAsync(new DatabaseUpdateException("other index violated", isDuplicate: true, constraintName: "ix_other"));

        // Act
        await _runner.RunAsync();

        // Assert
        _exceptions.ShouldHaveSingleItem().Reason.ShouldBe("other index violated");
    }

    [Test]
    public async Task RunAsync_OrderFailingWhileImporting_DiscardsItsTrackedChangesBeforeTheExceptionIsCommitted()
    {
        // Arrange
        GivenReceivedFile(NewOrder("ORD-1"));
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>())
            .ThrowsAsync(new InvalidOperationException("value too long"));

        // Act
        await _runner.RunAsync();

        // Assert
        _unitOfWork.Received(1).DiscardTrackedChanges();
        Received.InOrder(() =>
        {
            _unitOfWork.DiscardTrackedChanges();
            _exceptionRepository.Add(Arg.Any<ErpImportException>());
            _unitOfWork.CompleteAsync();
        });
    }

    [Test]
    public async Task RunAsync_EveryOrderRejectedButFileParseable_FinishesWithWarningsAndOneExceptionPerOrder()
    {
        // Arrange
        GivenReceivedFile(new OrderImportParseResult
        {
            Errors =
            [
                new OrderImportValidationError { Field = "Weekdays", Message = "no weekday", ExternalOrderReference = "ORD-1" },
                new OrderImportValidationError { Field = "Weekdays", Message = "no weekday", ExternalOrderReference = "ORD-2" }
            ]
        });

        // Act
        await _runner.RunAsync();

        // Assert
        _exceptions.Select(e => e.ExternalOrderReference).ShouldBe(new[] { "ORD-1", "ORD-2" });
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
        finish.Progress.ShouldBe(new ErpImportFileProgress(2, 2, 0, 0, 0, 0, 2));
    }

    [Test]
    public async Task RunAsync_MalformedXml_FinishesFailedWithTheParserMessageAndAlertsAdmins()
    {
        // Arrange
        var file = GivenReceivedFile(new OrderImportParseResult
        {
            Errors = [new OrderImportValidationError { Field = "root", Message = "Malformed XML: unexpected end" }]
        });

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.Failed);
        finish.Reason.ShouldBe("Malformed XML: unexpected end");
        finish.Progress.ShouldBe(ErpImportFileProgress.Empty);
        _heartbeats.ShouldBeEmpty();
        var exception = _exceptions.ShouldHaveSingleItem();
        exception.ExternalOrderReference.ShouldBeNull();
        exception.ImportFileId.ShouldBe(file.Id);
        await _triggerService.Received(1).OnEventAsync(
            Arg.Is<IAgentTriggerEvent>(e => e.Kind == AgentTriggerKinds.OrderImportFailed), Arg.Any<CancellationToken>());
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
    }

    [Test]
    public async Task RunAsync_ValidFileWithoutOrders_FinishesCompletedWithZeroTotal()
    {
        // Arrange
        GivenReceivedFile(new OrderImportParseResult());

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.Completed);
        finish.Progress.ShouldBe(new ErpImportFileProgress(0, 0, 0, 0, 0, 0, 0));
    }

    [Test]
    public async Task RunAsync_ImportReadsTheStoredFileExactlyOnceAndParsesTheVerifiedBytes()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));

        // Act
        await _runner.RunAsync();

        // Assert
        await _objectStorageService.Received(1).DownloadAsync(file.StorageKey, Arg.Any<CancellationToken>());
        await _objectStorageService.DidNotReceive().GetObjectInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _finishes.ShouldHaveSingleItem().Status.ShouldBe(ErpImportFileStatus.Completed);
    }

    [Test]
    public async Task RunAsync_StoredFileChangedAfterReceipt_FinishesFailedWithoutParsing()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        var changed = Encoding.UTF8.GetBytes(file.StorageKey.ToUpperInvariant());
        GivenStoredContent(file.StorageKey, changed);

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.Failed);
        finish.Reason.ShouldBe(ErpImportFileFailureReasons.FileChangedAfterReceipt);
        _parser.DidNotReceive().Parse(Arg.Any<Stream>());
        _exceptions.ShouldHaveSingleItem().Reason.ShouldBe(ErpImportFileFailureReasons.FileChangedAfterReceipt);
    }

    [Test]
    public async Task RunAsync_StoredFileGrownAfterReceipt_FinishesFailedAsChanged()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        GivenStoredContent(file.StorageKey, Encoding.UTF8.GetBytes(file.StorageKey + "<appended/>"));

        // Act
        await _runner.RunAsync();

        // Assert
        _finishes.ShouldHaveSingleItem().Reason.ShouldBe(ErpImportFileFailureReasons.FileChangedAfterReceipt);
        _parser.DidNotReceive().Parse(Arg.Any<Stream>());
    }

    [Test]
    public async Task RunAsync_StoredFileMissing_FinishesFailedWithItsOwnReason()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        _objectStorageService.DownloadAsync(file.StorageKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("gone"));

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.Failed);
        finish.Reason.ShouldBe(ErpImportFileFailureReasons.StoredFileMissing);
        _parser.DidNotReceive().Parse(Arg.Any<Stream>());
    }

    [Test]
    public async Task RunAsync_StoredFileLargerThanTheImportLimit_FinishesFailedWithoutReadingIt()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        file.SizeBytes = ErpImportFileLimits.MaxFileSizeBytes + 1;

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.Failed);
        finish.Reason.ShouldBe(ErpImportFileFailureReasons.FileTooLarge);
        await _objectStorageService.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RejectedOrdersWithoutReference_AreCountedOneByOne()
    {
        // Arrange
        GivenReceivedFile(new OrderImportParseResult
        {
            Orders = [NewOrder("ORD-1")],
            Errors =
            [
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "missing reference", OrderIndex = 1 },
                new OrderImportValidationError { Field = "FromDate", Message = "missing from date", OrderIndex = 1 },
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "missing reference", OrderIndex = 2 },
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "missing reference", OrderIndex = 3 }
            ]
        });

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
        finish.Progress.ShouldBe(new ErpImportFileProgress(4, 4, 1, 0, 0, 0, 3));
        _exceptions.Count.ShouldBe(3);
        _exceptions.ShouldAllBe(e => e.ExternalOrderReference == null);
        _exceptions[0].Reason.ShouldBe("missing reference; missing from date");
    }

    [Test]
    public async Task RunAsync_RepeatedDuplicateReference_CountsEveryRejectedDuplicate()
    {
        // Arrange
        GivenReceivedFile(new OrderImportParseResult
        {
            Orders = [NewOrder("ORD-1")],
            Errors =
            [
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "duplicate", ExternalOrderReference = "ORD-1", OrderIndex = 1 },
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "duplicate", ExternalOrderReference = "ORD-1", OrderIndex = 2 }
            ]
        });

        // Act
        await _runner.RunAsync();

        // Assert
        _finishes.ShouldHaveSingleItem().Progress.ShouldBe(new ErpImportFileProgress(3, 3, 1, 0, 0, 0, 2));
        _exceptions.Select(e => e.ExternalOrderReference).ShouldBe(new[] { "ORD-1", "ORD-1" });
    }

    [Test]
    public async Task RunAsync_OnlyOrdersWithoutReference_IsParseableAndFinishesWithWarnings()
    {
        // Arrange
        GivenReceivedFile(new OrderImportParseResult
        {
            Errors =
            [
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "missing reference", OrderIndex = 0 },
                new OrderImportValidationError { Field = "ExternalOrderReference", Message = "missing reference", OrderIndex = 1 }
            ]
        });

        // Act
        await _runner.RunAsync();

        // Assert
        var finish = _finishes.ShouldHaveSingleItem();
        finish.Status.ShouldBe(ErpImportFileStatus.CompletedWithWarnings);
        finish.Progress.ShouldBe(new ErpImportFileProgress(2, 2, 0, 0, 0, 0, 2));
    }

    [Test]
    public async Task RunAsync_UnreadableInboxFile_DoesNotStopTheRegistrationAndImportOfTheNextFile()
    {
        // Arrange
        var unreadableKey = Prefix + "a-locked.xml";
        var healthyKey = Prefix + "b-orders.xml";
        var content = Encoding.UTF8.GetBytes("healthy");
        _objectStorageService.ListAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new[] { unreadableKey, healthyKey });
        _fileRepository.GetKeyStatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportFileKeyState>());
        _objectStorageService.GetObjectInfoAsync(unreadableKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedAccessException("locked"));
        _objectStorageService.GetObjectInfoAsync(healthyKey, Arg.Any<CancellationToken>())
            .Returns(new StoredObjectInfo(content.Length, Sha256Of(content), Now.AddHours(-1)));
        string? renamedKey = null;
        _objectStorageService.TryMoveAsync(healthyKey, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                renamedKey = ci.ArgAt<string>(1);
                return true;
            });
        _objectStorageService.DownloadAsync(Arg.Is<string>(key => key.EndsWith("-b-orders.xml")), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(content));
        _fileRepository.Add(Arg.Do<ErpImportFile>(f => _receivedFiles.Add(f)));
        _parser.Parse(Arg.Is<Stream>(s => ReadsAs(s, content))).Returns(new OrderImportParseResult { Orders = [NewOrder("ORD-1")] });

        // Act
        await _runner.RunAsync();

        // Assert
        var registered = _receivedFiles.ShouldHaveSingleItem();
        registered.StorageKey.ShouldBe(renamedKey);
        registered.ReceivedAt.ShouldBe(Now.AddHours(-1));
        var finish = _finishes.ShouldHaveSingleItem();
        finish.FileId.ShouldBe(registered.Id);
        finish.Status.ShouldBe(ErpImportFileStatus.Completed);
        await _objectStorageService.DidNotReceive().TryMoveAsync(unreadableKey, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_InboxRegistrationFailingForADropPoint_StillImportsItsReceivedFiles()
    {
        // Arrange
        var file = GivenReceivedFile(NewOrder("ORD-1"));
        _objectStorageService.ListAsync(Prefix, Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedAccessException("inbox not readable"));

        // Act
        await _runner.RunAsync();

        // Assert
        _finishes.ShouldHaveSingleItem().FileId.ShouldBe(file.Id);
        _unitOfWork.Received().DiscardTrackedChanges();
    }

    [Test]
    public async Task RunAsync_ManyOrders_HeartbeatsAfterParsingAndEveryTwentyFiveOrders()
    {
        // Arrange
        GivenReceivedFile(Enumerable.Range(1, ManyOrders).Select(i => NewOrder($"ORD-{i}")).ToArray());

        // Act
        await _runner.RunAsync();

        // Assert
        _heartbeats.Select(h => h.ProcessedOrders).ShouldBe(new[] { 0, 25, 50 });
        _heartbeats.ShouldAllBe(h => h.TotalOrders == ManyOrders);
        _finishes.ShouldHaveSingleItem().Progress.ShouldBe(new ErpImportFileProgress(ManyOrders, ManyOrders, ManyOrders, 0, 0, 0, 0));
    }

    [Test]
    public async Task RunAsync_SlowOrders_HeartbeatOnceTheIntervalHasPassed()
    {
        // Arrange
        GivenReceivedFile(NewOrder("ORD-1"), NewOrder("ORD-2"), NewOrder("ORD-3"));
        _shiftRepository.When(r => r.AddWithSealedOrderHandling(Arg.Any<Shift>()))
            .Do(_ => _time.Now += ErpImportFileTiming.HeartbeatInterval);

        // Act
        await _runner.RunAsync();

        // Assert
        _heartbeats.Select(h => h.ProcessedOrders).ShouldBe(new[] { 0, 1, 2, 3 });
    }

    [Test]
    public async Task RunAsync_HeartbeatRejectedMidFile_AbandonsTheFileWithoutFinishing()
    {
        // Arrange
        GivenReceivedFile(Enumerable.Range(1, ManyOrders).Select(i => NewOrder($"ORD-{i}")).ToArray());
        _fileRepository.HeartbeatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Is<ErpImportFileProgress>(p => p.ProcessedOrders > 0), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.Received(ErpImportFileTiming.HeartbeatEveryOrders).AddWithSealedOrderHandling(Arg.Any<Shift>());
        _finishes.ShouldBeEmpty();
    }

    [Test]
    public async Task RunAsync_FirstHeartbeatRejected_ImportsNoOrder()
    {
        // Arrange
        GivenReceivedFile(NewOrder("ORD-1"));
        _fileRepository.HeartbeatAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ErpImportFileProgress>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await _runner.RunAsync();

        // Assert
        await _shiftRepository.DidNotReceive().AddWithSealedOrderHandling(Arg.Any<Shift>());
        _finishes.ShouldBeEmpty();
    }

    [Test]
    public async Task RunAsync_Cancelled_StopsWithoutFinishingOrRecordingARejection()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        GivenReceivedFile(NewOrder("ORD-1"), NewOrder("ORD-2"));
        _shiftRepository.When(r => r.AddWithSealedOrderHandling(Arg.Any<Shift>()))
            .Do(_ => cancellation.Cancel());

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => _runner.RunAsync(cancellation.Token));

        // Assert
        await _shiftRepository.Received(1).AddWithSealedOrderHandling(Arg.Any<Shift>());
        _finishes.ShouldBeEmpty();
        _exceptions.ShouldBeEmpty();
        _runState.IsRunning.ShouldBeFalse();
    }

    [Test]
    public async Task RunAsync_UnexpectedErrorInOneFile_FinishesItInterruptedAndImportsTheNextFile()
    {
        // Arrange
        var broken = GivenReceivedFile(NewOrder("ORD-1"));
        var healthy = GivenReceivedFile(NewOrder("ORD-2"));
        _objectStorageService.DownloadAsync(broken.StorageKey, Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk gone"));

        // Act
        await _runner.RunAsync();

        // Assert
        _finishes.Count.ShouldBe(2);
        var interrupted = _finishes.Single(f => f.FileId == broken.Id);
        interrupted.Status.ShouldBe(ErpImportFileStatus.Interrupted);
        interrupted.Reason.ShouldBe(ErpImportFileFailureReasons.ImportAbortedPrefix + "disk gone");
        _finishes.Single(f => f.FileId == healthy.Id).Status.ShouldBe(ErpImportFileStatus.Completed);
        _unitOfWork.Received(1).DiscardTrackedChanges();
        _runState.IsRunning.ShouldBeFalse();
    }

    private void GivenNotYetDue()
    {
        _settingsRepository.GetSettingNoTracking(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new Settings { Type = ErpImportSettingsTypes.NextRunUtc, Value = DateTime.UtcNow.AddHours(1).ToString("O") });
    }

    private ErpImportFile GivenReceivedFile(params ImportedOrderPayload[] orders)
    {
        return GivenReceivedFile(new OrderImportParseResult { Orders = orders.ToList() });
    }

    private ErpImportFile GivenReceivedFile(OrderImportParseResult parseResult)
    {
        var storageKey = $"{Prefix}{Guid.NewGuid():N}-orders.xml";
        var content = Encoding.UTF8.GetBytes(storageKey);
        var file = new ErpImportFile
        {
            Id = Guid.NewGuid(),
            DropPointId = DropPoint.Id,
            SourceSystemId = DropPoint.SourceSystemId,
            StorageKey = storageKey,
            FileName = "orders.xml",
            SizeBytes = content.Length,
            Sha256 = Sha256Of(content),
            Status = ErpImportFileStatus.Received,
            ReceivedAt = Now.AddMinutes(-_receivedFiles.Count - 1)
        };
        _receivedFiles.Add(file);

        GivenStoredContent(file.StorageKey, content);
        _parser.Parse(Arg.Is<Stream>(s => ReadsAs(s, content))).Returns(parseResult);

        return file;
    }

    private void GivenStoredContent(string storageKey, byte[] content)
    {
        _objectStorageService.DownloadAsync(storageKey, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(content));
    }

    private static string Sha256Of(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static bool ReadsAs(Stream stream, byte[] expected)
    {
        if (stream is not MemoryStream memory)
        {
            return false;
        }

        return memory.ToArray().SequenceEqual(expected);
    }

    private void GivenActiveShift(Shift shift)
    {
        _shiftRepository.FindActiveByExternalReferenceAsync(shift.SourceSystemId!, shift.ExternalOrderReference!, Arg.Any<CancellationToken>())
            .Returns(shift);
        _shiftRepository.CutList(shift.Id, null, false).Returns([shift]);
    }

    private Shift SealedOrderFor(ImportedOrderPayload order)
    {
        var sealedOrder = ImportedOrderShiftMapper.BuildDraft(order, _customer.Id);
        sealedOrder.Status = ShiftStatus.SealedOrder;
        return sealedOrder;
    }

    private static ImportedOrderPayload NewOrder(string reference) => new()
    {
        SourceSystemId = "erp-1",
        ExternalOrderReference = reference,
        Customer = new ImportedCustomerPayload { Company = "Spitex Musterhausen", Street = "Hauptstrasse 5", Zip = "4000" },
        Description = "Morning care visit",
        FromDate = new DateOnly(2026, 8, 1),
        StartTime = new TimeOnly(7, 0),
        EndTime = new TimeOnly(15, 0),
        IsMonday = true,
        SumEmployees = 2
    };
}
