// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollPeriodExportService with real repositories on an in-memory EF Core database: a blocked
/// period throws and writes and uploads nothing; a first export contains every person at revision 1; a second run
/// exports only the changed person at revision 2 as supplementary (file name suffix, flag, item flag); unchanged
/// content is "nothing new"; A to B to A exports A again at revision 3; a person selection scopes gate and loader;
/// a failed commit deletes the uploaded artifact and a duplicate revision becomes PayrollExportConcurrentException;
/// an on-call day entry keeps its Days unit; preview and export share the selection and validation.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Exports;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Exports;

[TestFixture]
public class PayrollPeriodExportServiceTests
{
    private const string Format = PayrollExportConstants.FormatKeyDatevLug;
    private const string Actor = "tester";

    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly Until = new(2026, 1, 31);

    private readonly Guid _personA = Guid.NewGuid();
    private readonly Guid _personB = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private IPayrollExportFormatter _formatter = null!;
    private IExportFormatPolicy _policy = null!;
    private IPayrollCompletenessGate _gate = null!;
    private IPayrollExportDataLoader _loader = null!;
    private IPayrollExportConfigRepository _configRepository = null!;
    private IExportFormatOverrideApplier _overrideApplier = null!;
    private IPayrollArtifactStorage _storage = null!;
    private Dictionary<Guid, List<PayrollDayEntry>> _entriesByPerson = null!;
    private PayrollCompletenessResult _completeness = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _formatter = Substitute.For<IPayrollExportFormatter>();
        _formatter.FormatKey.Returns(Format);
        _formatter.ContentType.Returns(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.Returns(PayrollExportConstants.FileExtensionCsv);
        _formatter.Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>())
            .Returns(new PayrollExportResult { Content = [1, 2, 3], RecordCount = 2 });

        _policy = Substitute.For<IExportFormatPolicy>();
        _policy.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        _completeness = new PayrollCompletenessResult();
        _gate = Substitute.For<IPayrollCompletenessGate>();
        _gate.CheckAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => _completeness);

        _entriesByPerson = new Dictionary<Guid, List<PayrollDayEntry>>
        {
            [_personA] = [Entry(1, 8m)],
            [_personB] = [Entry(2, 6m)]
        };
        _loader = Substitute.For<IPayrollExportDataLoader>();
        _loader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => LoadData(ci.ArgAt<IReadOnlyCollection<Guid>?>(2)));

        _configRepository = Substitute.For<IPayrollExportConfigRepository>();
        _configRepository.GetAsync(Arg.Any<CancellationToken>())
            .Returns(new PayrollExportGroupConfig { TargetSystem = Format });

        _overrideApplier = Substitute.For<IExportFormatOverrideApplier>();
        _storage = Substitute.For<IPayrollArtifactStorage>();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    private PayrollPeriodExportService CreateService(IUnitOfWork? unitOfWork = null)
    {
        return new PayrollPeriodExportService(
            [_formatter],
            _policy,
            _gate,
            _loader,
            new ExportLogItemRepository(_context),
            new ExportLogRepository(_context),
            _configRepository,
            _overrideApplier,
            _storage,
            unitOfWork ?? new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>()),
            Substitute.For<ILogger<PayrollPeriodExportService>>());
    }

    private static PayrollDayEntry Entry(int day, decimal hours, PayrollQuantityUnit unit = PayrollQuantityUnit.Hours) => new()
    {
        Date = new DateOnly(2026, 1, day),
        Kind = PayrollEntryKind.WorkHours,
        Quantity = hours,
        Unit = unit
    };

    private PayrollExportData LoadData(IReadOnlyCollection<Guid>? clientIds)
    {
        var employees = _entriesByPerson
            .Where(p => clientIds is null || clientIds.Contains(p.Key))
            .Select((p, index) => new PayrollEmployee
            {
                ClientId = p.Key,
                IdNumber = 100 + index,
                FullName = $"Person {index}",
                Entries = p.Value.ToList()
            })
            .ToList();

        return new PayrollExportData { StartDate = From, EndDate = Until, Employees = employees };
    }

    private Task<PayrollExportOutcome> Export(IReadOnlyCollection<Guid>? clientIds = null, PayrollPeriodExportService? service = null)
    {
        return (service ?? CreateService()).ExportAsync(From, Until, Format, "de", clientIds, Actor, CancellationToken.None);
    }

    private static PayrollCompletenessResult Blocked() => new()
    {
        BlockerTotal = 1,
        Blockers = [new PayrollExportBlockerDto { Reason = Klacks.Api.Domain.Enums.PayrollExportBlockReason.DayNotLocked }]
    };

    [Test]
    public async Task BlockedPeriod_ThrowsAndWritesAndUploadsNothing()
    {
        _completeness = Blocked();

        var thrown = await Should.ThrowAsync<PayrollExportBlockedException>(() => Export());

        thrown.Completeness.BlockerTotal.ShouldBe(1);
        await _storage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        _formatter.DidNotReceive().Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>());
        (await _context.ExportLog.CountAsync()).ShouldBe(0);
        (await _context.ExportLogItem.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task PeriodLongerThanTheMaximum_IsRejectedBeforeAnythingIsLoaded()
    {
        var service = CreateService();
        var tooLongUntil = From.AddDays(PayrollExportConstants.MaxPeriodDays);

        await Should.ThrowAsync<InvalidRequestException>(() =>
            service.ExportAsync(From, tooLongUntil, Format, "de", null, Actor, CancellationToken.None));
        await Should.ThrowAsync<InvalidRequestException>(() =>
            service.PreviewAsync(From, tooLongUntil, Format, null, CancellationToken.None));

        await _gate.DidNotReceiveWithAnyArgs().CheckAsync(default, default, default, default);
        await _loader.DidNotReceiveWithAnyArgs().LoadAsync(default, default, default, default);
    }

    [Test]
    public async Task PeriodOfExactlyTheMaximum_IsAccepted()
    {
        var service = CreateService();
        var maxUntil = From.AddDays(PayrollExportConstants.MaxPeriodDays - 1);

        var outcome = await service.ExportAsync(From, maxUntil, Format, "de", null, Actor, CancellationToken.None);

        outcome.PersonCount.ShouldBe(2);
    }

    [Test]
    public async Task LanguageLongerThanTheColumn_IsRejectedBeforeTheUpload()
    {
        var tooLong = new string('x', ExportLogLimits.LanguageMaxLength + 1);

        await Should.ThrowAsync<InvalidRequestException>(() =>
            CreateService().ExportAsync(From, Until, Format, tooLong, null, Actor, CancellationToken.None));

        await _storage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        (await _context.ExportLog.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task FirstExport_ContainsEveryPersonAtRevisionOne_AndStoresTheArtifact()
    {
        var outcome = await Export();

        outcome.PersonCount.ShouldBe(2);
        outcome.IsSupplementary.ShouldBeFalse();
        outcome.FileName.ShouldBe("payroll-export_2026-01-01_2026-01-31.csv");
        outcome.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        outcome.FileContent.ShouldBe(new byte[] { 1, 2, 3 });

        var items = await _context.ExportLogItem.ToListAsync();
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.Revision == 1 && !i.IsSupplementary && i.ExportLogId == outcome.ExportLogId);
        items.ShouldAllBe(i => i.ContentHash.Length == 64 && i.EntryCount == 1 && i.Format == Format);

        var log = await _context.ExportLog.SingleAsync();
        log.Id.ShouldBe(outcome.ExportLogId);
        log.GroupId.ShouldBeNull();
        log.PersonCount.ShouldBe(2);
        log.IsSupplementary.ShouldBeFalse();
        log.ExportedBy.ShouldBe(Actor);
        log.StorageKey.ShouldBe($"payroll-export/20260101-20260131/{outcome.ExportLogId}.csv");

        await _storage.Received(1).UploadAsync(log.StorageKey!, Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SecondExportAfterChange_ExportsOnlyTheChangedPersonAsSupplementary()
    {
        await Export();
        _entriesByPerson[_personA] = [Entry(1, 9m)];

        var outcome = await Export();

        outcome.PersonCount.ShouldBe(1);
        outcome.IsSupplementary.ShouldBeTrue();
        outcome.FileName.ShouldBe("payroll-export_2026-01-01_2026-01-31_supplement.csv");
        _formatter.Received(1).Format(
            Arg.Is<PayrollExportData>(d => d.Employees.Count == 1 && d.Employees[0].ClientId == _personA),
            Arg.Any<PayrollExportGroupConfig>());

        var secondItem = await _context.ExportLogItem.SingleAsync(i => i.ExportLogId == outcome.ExportLogId);
        secondItem.ClientId.ShouldBe(_personA);
        secondItem.Revision.ShouldBe(2);
        secondItem.IsSupplementary.ShouldBeTrue();
        (await _context.ExportLog.SingleAsync(l => l.Id == outcome.ExportLogId)).IsSupplementary.ShouldBeTrue();
    }

    [Test]
    public async Task NothingChanged_ThrowsNothingNew_AndLeavesNoNewRowOrUpload()
    {
        await Export();
        _storage.ClearReceivedCalls();

        await Should.ThrowAsync<PayrollExportNothingNewException>(() => Export());

        await _storage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        (await _context.ExportLog.CountAsync()).ShouldBe(1);
        (await _context.ExportLogItem.CountAsync()).ShouldBe(2);
    }

    [Test]
    public async Task ContentGoingFromAToBBackToA_ExportsAAgainAtRevisionThree()
    {
        await Export();
        var original = _entriesByPerson[_personA];

        _entriesByPerson[_personA] = [Entry(1, 9m)];
        await Export();

        _entriesByPerson[_personA] = original;
        var outcome = await Export();

        outcome.PersonCount.ShouldBe(1);
        outcome.IsSupplementary.ShouldBeTrue();
        var third = await _context.ExportLogItem.SingleAsync(i => i.ExportLogId == outcome.ExportLogId);
        third.ClientId.ShouldBe(_personA);
        third.Revision.ShouldBe(3);
    }

    [Test]
    public async Task PersonSelection_ScopesGateAndLoader_AndExportsOnlyTheSelectedPerson()
    {
        IReadOnlyCollection<Guid> selection = [_personB, _personB];

        var outcome = await Export(selection);

        outcome.PersonCount.ShouldBe(1);
        await _gate.Received(1).CheckAsync(
            From, Until, Arg.Is<IReadOnlyCollection<Guid>?>(c => c != null && c.Count == 1 && c.Contains(_personB)), Arg.Any<CancellationToken>());
        await _loader.Received(1).LoadAsync(
            From, Until, Arg.Is<IReadOnlyCollection<Guid>?>(c => c != null && c.Count == 1 && c.Contains(_personB)), Arg.Any<CancellationToken>());
        (await _context.ExportLogItem.SingleAsync()).ClientId.ShouldBe(_personB);
    }

    [Test]
    public async Task EmptyPersonSelection_IsRejected_NotTreatedAsEveryone()
    {
        await Should.ThrowAsync<InvalidRequestException>(() => Export(new List<Guid>()));

        await _loader.DidNotReceive().LoadAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoClosedData_IsAnInvalidRequest_NotNothingNew()
    {
        _entriesByPerson.Clear();

        await Should.ThrowAsync<InvalidRequestException>(() => Export());
    }

    [Test]
    public async Task DuplicateRevision_BecomesConcurrentException_AndDeletesTheUploadedArtifact()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(Task.FromException(
            new DatabaseUpdateException("duplicate", null, isDuplicate: true)));

        await Should.ThrowAsync<PayrollExportConcurrentException>(() => Export(service: CreateService(unitOfWork)));

        await _storage.Received(1).UploadAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _storage.Received(1).DeleteAsync(
            Arg.Is<string>(k => k.StartsWith("payroll-export/20260101-20260131/")), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OtherCommitFailure_DeletesTheArtifactAndRethrowsTheOriginalException()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(Task.FromException(new InvalidOperationException("boom")));

        await Should.ThrowAsync<InvalidOperationException>(() => Export(service: CreateService(unitOfWork)));

        await _storage.Received(1).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailedArtifactDelete_DoesNotMaskTheCommitFailure()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(Task.FromException(new DatabaseUpdateException("duplicate", null, isDuplicate: true)));
        _storage.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("locked")));

        await Should.ThrowAsync<PayrollExportConcurrentException>(() => Export(service: CreateService(unitOfWork)));
    }

    [Test]
    public async Task UploadFailure_WritesNoLogRow()
    {
        _storage.UploadAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));

        await Should.ThrowAsync<IOException>(() => Export());

        (await _context.ExportLog.CountAsync()).ShouldBe(0);
        (await _context.ExportLogItem.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task OnCallDayEntry_StaysInDays_InTheFormatterInputAndTheSnapshot()
    {
        _entriesByPerson.Clear();
        _entriesByPerson[_personA] = [Entry(3, 1m, PayrollQuantityUnit.Days)];

        await Export();

        _formatter.Received(1).Format(
            Arg.Is<PayrollExportData>(d => d.Employees[0].Entries[0].Unit == PayrollQuantityUnit.Days
                && d.Employees[0].Entries[0].Quantity == 1m),
            Arg.Any<PayrollExportGroupConfig>());
        var item = await _context.ExportLogItem.SingleAsync();
        item.EntriesJson.ShouldContain("\"Unit\":1");
    }

    [Test]
    public async Task SkipCounters_AreRecordedOnTheLog_AndReturned()
    {
        _overrideApplier.ApplyAsync(Format, Arg.Any<PayrollExportGroupConfig>(), Arg.Any<CancellationToken>()).Returns(true);
        _formatter.Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>())
            .Returns(new PayrollExportResult
            {
                Content = [1],
                RecordCount = 1,
                SkippedAbsenceCount = 2,
                SkippedUnsupportedUnitCount = 3,
                AbsenceMappingInvalid = true
            });

        var outcome = await Export();

        outcome.SkippedEntryCount.ShouldBe(5);
        outcome.AbsenceMappingInvalid.ShouldBeTrue();
        var log = await _context.ExportLog.SingleAsync();
        log.OverrideApplied.ShouldBeTrue();
        log.SkippedAbsenceCount.ShouldBe(2);
        log.SkippedUnsupportedUnitCount.ShouldBe(3);
        log.AbsenceMappingInvalid.ShouldBeTrue();
        log.RecordCount.ShouldBe(1);
        log.FileSize.ShouldBe(1);
    }

    [Test]
    public async Task UnknownOrDisabledFormat_AndInvertedPeriod_AreInvalidRequests()
    {
        var service = CreateService();

        await Should.ThrowAsync<InvalidRequestException>(() =>
            service.ExportAsync(From, Until, "does-not-exist", "de", null, Actor, CancellationToken.None));
        await Should.ThrowAsync<InvalidRequestException>(() =>
            service.ExportAsync(From, Until, " ", "de", null, Actor, CancellationToken.None));
        await Should.ThrowAsync<InvalidRequestException>(() =>
            service.ExportAsync(Until, From, Format, "de", null, Actor, CancellationToken.None));

        _policy.IsEnabledAsync(Format, Arg.Any<CancellationToken>()).Returns(false);
        await Should.ThrowAsync<InvalidRequestException>(() => Export());
    }

    [Test]
    public async Task Preview_ListsNewPersons_ThenOnlyChangedOnes_AndWritesNothing()
    {
        var service = CreateService();

        var first = await service.PreviewAsync(From, Until, Format, null);
        first.CanExport.ShouldBeTrue();
        first.IsComplete.ShouldBeTrue();
        first.PersonCount.ShouldBe(2);
        first.AlreadyExportedCount.ShouldBe(0);
        first.NewOrChangedPersons.Count.ShouldBe(2);
        first.NewOrChangedPersons.ShouldAllBe(p => p.IsNew && p.PreviousRevision == null);
        (await _context.ExportLog.CountAsync()).ShouldBe(0);
        await _storage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());

        await Export(service: service);
        _entriesByPerson[_personB] = [Entry(2, 7m)];

        var second = await service.PreviewAsync(From, Until, Format, null);
        second.CanExport.ShouldBeTrue();
        second.PersonCount.ShouldBe(2);
        second.AlreadyExportedCount.ShouldBe(1);
        var changed = second.NewOrChangedPersons.ShouldHaveSingleItem();
        changed.ClientId.ShouldBe(_personB);
        changed.IsNew.ShouldBeFalse();
        changed.PreviousRevision.ShouldBe(1);
    }

    [Test]
    public async Task Preview_WhenBlocked_ReportsBlockersAndCannotExport_ButStillListsPersons()
    {
        _completeness = Blocked();

        var preview = await CreateService().PreviewAsync(From, Until, Format, null);

        preview.CanExport.ShouldBeFalse();
        preview.IsComplete.ShouldBeFalse();
        preview.BlockerTotal.ShouldBe(1);
        preview.Blockers.Count.ShouldBe(1);
        preview.NewOrChangedPersons.Count.ShouldBe(2);
    }

    [Test]
    public async Task Preview_WhenNothingIsNew_CannotExport()
    {
        var service = CreateService();
        await Export(service: service);

        var preview = await service.PreviewAsync(From, Until, Format, null);

        preview.CanExport.ShouldBeFalse();
        preview.IsComplete.ShouldBeTrue();
        preview.NewOrChangedPersons.ShouldBeEmpty();
        preview.AlreadyExportedCount.ShouldBe(2);
    }
}
