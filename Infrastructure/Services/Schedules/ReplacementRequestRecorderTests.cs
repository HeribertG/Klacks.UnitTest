// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the replacement request book writer: one Proposed row per covered slot, no duplicate for a slot
/// that is recorded again, raw time facts (report clamped to now, slot start from the company zone), and the
/// manual replacement path (Accepted, scenario token kept, clone shift mapped to its source, engine-made changes
/// and corrections ignored). Runs over the real repository on an in-memory context.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ReplacementRequestRecorderTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid AbsentId = Guid.NewGuid();
    private static readonly Guid CandidateId = Guid.NewGuid();
    private static readonly Guid OtherCandidateId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid OtherShiftId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private const string CandidatePhone = "+41 79 123 45 67";

    private DataBaseContext _context = null!;
    private IShiftRepository _shiftRepository = null!;
    private IReplacementContactPhoneResolver _phoneResolver = null!;
    private ReplacementRequestRecorder _recorder = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.GetNoTracking(Arg.Any<Guid>()).Returns((Shift?)null);

        _phoneResolver = Substitute.For<IReplacementContactPhoneResolver>();
        _phoneResolver.ResolveAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [CandidateId] = CandidatePhone });

        var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        _recorder = new ReplacementRequestRecorder(
            new ReplacementRequestRepository(_context, Substitute.For<ILogger<ReplacementRequest>>()),
            _shiftRepository,
            _phoneResolver,
            new FixedCompanyClock(new DateTimeOffset(Now), zurich),
            new SettableTimeProvider(Now),
            Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task RecordProposals_StagesOneProposedRowPerCoveredSlot_WithTheScenarioToken()
    {
        var token = Guid.NewGuid();

        var result = await _recorder.RecordProposalsAsync(
            Context(token),
            [Slot(CandidateId, ShiftId), Slot(OtherCandidateId, OtherShiftId)]);
        await _context.SaveChangesAsync();

        var rows = await _context.ReplacementRequests.ToListAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.AnalyseToken == token
            && r.Outcome == ReplacementRequestOutcome.Proposed
            && r.Source == ReplacementRequestSource.PlannerDialog
            && r.AbsentClientId == AbsentId
            && r.AbsenceId == AbsenceId
            && r.GroupId == GroupId
            && r.AppliedAtUtc == null);
        result.Select(s => s.RequestId).ShouldBe(rows.Select(r => (Guid?)r.Id), ignoreOrder: true);
        result.Single(s => s.ReplacementClientId == CandidateId).Phone.ShouldBe(CandidatePhone);
        result.Single(s => s.ReplacementClientId == OtherCandidateId).Phone.ShouldBeNull();
    }

    [Test]
    public async Task RecordProposals_SameSlotRecordedAgain_ReusesTheRowInsteadOfAddingASecond()
    {
        var token = Guid.NewGuid();

        var first = await _recorder.RecordProposalsAsync(Context(token), [Slot(CandidateId, ShiftId)]);
        var second = await _recorder.RecordProposalsAsync(Context(token), [Slot(CandidateId, ShiftId)]);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.CountAsync()).ShouldBe(1);
        second[0].RequestId.ShouldBe(first[0].RequestId);
    }

    [Test]
    public async Task RecordProposals_SlotWithoutShift_IsReturnedUnchangedAndNotStored()
    {
        var result = await _recorder.RecordProposalsAsync(Context(Guid.NewGuid()), [Slot(CandidateId, Guid.Empty)]);
        await _context.SaveChangesAsync();

        result[0].RequestId.ShouldBeNull();
        (await _context.ReplacementRequests.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task RecordProposals_StoresRawTimes_ReportClampedToNowAndSlotStartFromCompanyZone()
    {
        await _recorder.RecordProposalsAsync(
            Context(Guid.NewGuid(), reportedAtUtc: Now.AddHours(3)), [Slot(CandidateId, ShiftId)]);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.ReportedAtUtc.ShouldBe(Now);
        row.ShiftStartUtc.ShouldBe(new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task RecordProposals_PastReport_IsStoredAsGiven()
    {
        var reported = Now.AddHours(-2);

        await _recorder.RecordProposalsAsync(Context(Guid.NewGuid(), reportedAtUtc: reported), [Slot(CandidateId, ShiftId)]);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.SingleAsync()).ReportedAtUtc.ShouldBe(reported);
    }

    [Test]
    public async Task RecordManual_ReplacementInScenario_CarriesTheTokenAndTheSourceShift()
    {
        var token = Guid.NewGuid();
        var cloneShiftId = Guid.NewGuid();
        _shiftRepository.GetNoTracking(cloneShiftId).Returns(new Shift { Id = cloneShiftId, ScenarioSourceShiftId = ShiftId });
        var work = Work(cloneShiftId, token);
        var change = Change(work, WorkChangeType.ReplacementWithin, token);

        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.AnalyseToken.ShouldBe(token);
        row.ShiftId.ShouldBe(ShiftId);
        row.Source.ShouldBe(ReplacementRequestSource.ManualReplacement);
        row.Outcome.ShouldBe(ReplacementRequestOutcome.Accepted);
        row.OutcomeAtUtc.ShouldBe(Now);
        row.WorkChangeId.ShouldBe(change.Id);
        row.AppliedAtUtc.ShouldBeNull();
        row.AbsentClientId.ShouldBe(AbsentId);
        row.CandidateClientId.ShouldBe(CandidateId);
    }

    [Test]
    public async Task RecordManual_ReplacementOnTheRealPlan_IsAppliedImmediately()
    {
        var work = Work(ShiftId, null);

        await _recorder.RecordManualReplacementAsync(work, Change(work, WorkChangeType.ReplacementWithin, null));
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.AnalyseToken.ShouldBeNull();
        row.AppliedAtUtc.ShouldBe(Now);
    }

    [Test]
    public async Task RecordManual_ReplacementStart_StoresTheEffectiveWindowNotTheEmptyStoredTimes()
    {
        var work = Work(ShiftId, null);
        var change = Change(work, WorkChangeType.ReplacementStart, null);
        change.StartTime = TimeOnly.MinValue;
        change.EndTime = TimeOnly.MinValue;
        change.ChangeTime = 3m;

        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.StartTime.ShouldBe(new TimeOnly(8, 0));
        row.EndTime.ShouldBe(new TimeOnly(11, 0));
    }

    [Test]
    public async Task RecordManual_ChangeMaterialisedByTheRecoveryEngine_IsLeftToCoverAbsence()
    {
        var work = Work(ShiftId, Guid.NewGuid());
        var change = Change(work, WorkChangeType.ReplacementWithin, work.AnalyseToken);
        change.Description = RecoveryMarkers.WorkChangeSource;

        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task RecordManual_CorrectionOrChangeWithoutReplacement_RecordsNothing()
    {
        var work = Work(ShiftId, null);
        var correction = Change(work, WorkChangeType.CorrectionEnd, null);
        var noReplacement = Change(work, WorkChangeType.ReplacementWithin, null);
        noReplacement.ReplaceClientId = null;

        await _recorder.RecordManualReplacementAsync(work, correction);
        await _recorder.RecordManualReplacementAsync(work, noReplacement);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task RecordManual_SameReplacementTwice_KeepsOneRow()
    {
        var work = Work(ShiftId, null);

        await _recorder.RecordManualReplacementAsync(work, Change(work, WorkChangeType.ReplacementStart, null));
        await _recorder.RecordManualReplacementAsync(work, Change(work, WorkChangeType.ReplacementEnd, null));
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task RecordManual_OnASlotTheEngineAlreadyProposed_DoesNotCollideWithTheProposal()
    {
        var token = Guid.NewGuid();
        await _recorder.RecordProposalsAsync(Context(token), [Slot(CandidateId, ShiftId)]);
        var work = Work(ShiftId, token);

        await _recorder.RecordManualReplacementAsync(work, Change(work, WorkChangeType.ReplacementWithin, token));
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.SingleAsync()).Outcome.ShouldBe(ReplacementRequestOutcome.Proposed);
    }

    [Test]
    public async Task RecordManual_ReplacementEndOfANightShift_StartsAfterMidnightOnTheNextDay()
    {
        var work = Work(ShiftId, null);
        work.StartTime = new TimeOnly(22, 0);
        work.EndTime = new TimeOnly(6, 0);
        var change = Change(work, WorkChangeType.ReplacementEnd, null);
        change.StartTime = TimeOnly.MinValue;
        change.EndTime = TimeOnly.MinValue;
        change.ChangeTime = 3m;

        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.StartTime.ShouldBe(new TimeOnly(3, 0));
        row.EndTime.ShouldBe(new TimeOnly(6, 0));
        row.ShiftStartUtc.ShouldBe(new DateTime(2026, 3, 11, 2, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task RecordManual_ChangeTimeLongerThanTheWork_IsClampedToTheWork()
    {
        var work = Work(ShiftId, null);
        var change = Change(work, WorkChangeType.ReplacementStart, null);
        change.ChangeTime = 30m;

        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.StartTime.ShouldBe(new TimeOnly(8, 0));
        row.EndTime.ShouldBe(new TimeOnly(16, 0));
    }

    [Test]
    public async Task RecordProposals_ReportFarBeforeTheSlot_IsRaisedToTheLeadWindow()
    {
        await _recorder.RecordProposalsAsync(
            Context(Guid.NewGuid(), reportedAtUtc: Now.AddDays(-400)), [Slot(CandidateId, ShiftId)]);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.ReportedAtUtc.ShouldBe(new DateTime(2025, 12, 7, 23, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task SyncManual_UpdatedReplacement_MovesCandidateAndTimes()
    {
        var work = Work(ShiftId, null);
        var change = Change(work, WorkChangeType.ReplacementWithin, null);
        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        change.ReplaceClientId = OtherCandidateId;
        change.StartTime = new TimeOnly(12, 0);
        await _recorder.SyncManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync();
        row.CandidateClientId.ShouldBe(OtherCandidateId);
        row.StartTime.ShouldBe(new TimeOnly(12, 0));
        row.ShiftStartUtc.ShouldBe(new DateTime(2026, 3, 10, 11, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task SyncManual_ChangeNoLongerAReplacement_SoftDeletesTheRow()
    {
        var work = Work(ShiftId, null);
        var change = Change(work, WorkChangeType.ReplacementWithin, null);
        await _recorder.RecordManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        change.Type = WorkChangeType.CorrectionEnd;
        await _recorder.SyncManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.CountAsync()).ShouldBe(0);
        (await _context.ReplacementRequests.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
    }

    [Test]
    public async Task SyncManual_CorrectionTurnedIntoAReplacement_RecordsIt()
    {
        var work = Work(ShiftId, null);
        var change = Change(work, WorkChangeType.ReplacementWithin, null);

        await _recorder.SyncManualReplacementAsync(work, change);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.SingleAsync()).WorkChangeId.ShouldBe(change.Id);
    }

    [Test]
    public async Task DiscardManual_DeletedReplacement_SoftDeletesOnlyItsRow()
    {
        var work = Work(ShiftId, null);
        var change = Change(work, WorkChangeType.ReplacementWithin, null);
        await _recorder.RecordManualReplacementAsync(work, change);
        await _recorder.RecordProposalsAsync(Context(Guid.NewGuid()), [Slot(OtherCandidateId, ShiftId)]);
        await _context.SaveChangesAsync();

        await _recorder.DiscardManualReplacementAsync(change.Id);
        await _context.SaveChangesAsync();

        (await _context.ReplacementRequests.SingleAsync()).Source.ShouldBe(ReplacementRequestSource.PlannerDialog);
    }
    private static ReplacementProposalContext Context(Guid token, DateTime? reportedAtUtc = null)
        => new(AbsentId, GroupId, AbsenceId, token, ReplacementRequestSource.PlannerDialog, reportedAtUtc);

    private static CoveredSlot Slot(Guid candidateId, Guid shiftId)
        => new(shiftId, Day, candidateId, "Bob", 0, Guid.NewGuid(), new TimeOnly(8, 0), new TimeOnly(16, 0));

    private static Work Work(Guid shiftId, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = AbsentId,
        ShiftId = shiftId,
        CurrentDate = Day,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        AnalyseToken = token
    };

    private static WorkChange Change(Work work, WorkChangeType type, Guid? token) => new()
    {
        Id = Guid.NewGuid(),
        WorkId = work.Id,
        Type = type,
        ReplaceClientId = CandidateId,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        ChangeTime = 8m,
        AnalyseToken = token
    };
}
