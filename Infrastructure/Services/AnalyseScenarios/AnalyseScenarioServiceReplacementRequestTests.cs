// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the replacement request book hooks of the scenario lifecycle: accepting a scenario stamps
/// AppliedAtUtc and the WorkChange id on the rows whose replacement is promoted (clone shift mapped back to the
/// source shift), leaves rows without a promoted change unapplied and changes nothing on a second run; rejecting
/// soft-deletes only the untouched Proposed rows and keeps recorded answers.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceReplacementRequestTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly Guid AbsentId = Guid.NewGuid();
    private static readonly Guid CandidateId = Guid.NewGuid();
    private static readonly Guid AlternativeId = Guid.NewGuid();
    private static readonly Guid SourceShiftId = Guid.NewGuid();
    private static readonly Guid CloneShiftId = Guid.NewGuid();

    private DataBaseContext _context = null!;
    private AnalyseScenarioService _service = null!;
    private Guid _token;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _service = new AnalyseScenarioService(_context);
        _token = Guid.NewGuid();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Promote_StampsTheRowWhoseReplacementIsPromoted_AndLeavesAnOnlyPhonedAlternativeUnapplied()
    {
        var workChangeId = await SeedScenarioReplacementAsync();
        var proposal = await AddRowAsync(CandidateId, ReplacementRequestOutcome.Accepted);
        var alternative = await AddRowAsync(AlternativeId, ReplacementRequestOutcome.Declined);

        await _service.PromoteScenarioWorksAsync(_token, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();

        var stamped = await _context.ReplacementRequests.SingleAsync(r => r.Id == proposal);
        stamped.AppliedAtUtc.ShouldNotBeNull();
        stamped.WorkChangeId.ShouldBe(workChangeId);
        stamped.AnalyseToken.ShouldBe(_token);
        var unapplied = await _context.ReplacementRequests.SingleAsync(r => r.Id == alternative);
        unapplied.AppliedAtUtc.ShouldBeNull();
        unapplied.WorkChangeId.ShouldBeNull();
    }

    [Test]
    public async Task Promote_DeclinedOrNotReachedRow_IsNotStampedEvenWhenThePersonWasApplied()
    {
        await SeedScenarioReplacementAsync();
        var declined = await AddRowAsync(CandidateId, ReplacementRequestOutcome.Declined);

        await _service.PromoteScenarioWorksAsync(_token, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync(r => r.Id == declined);
        row.AppliedAtUtc.ShouldBeNull();
        row.Outcome.ShouldBe(ReplacementRequestOutcome.Declined);
    }
    [Test]
    public async Task Promote_RowAlreadyApplied_IsNotStampedAgain()
    {
        await SeedScenarioReplacementAsync();
        var appliedAt = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
        var keptWorkChangeId = Guid.NewGuid();
        var rowId = await AddRowAsync(CandidateId, ReplacementRequestOutcome.Accepted, appliedAt, keptWorkChangeId);

        await _service.PromoteScenarioWorksAsync(_token, Day, Day, CancellationToken.None);
        await _context.SaveChangesAsync();

        var row = await _context.ReplacementRequests.SingleAsync(r => r.Id == rowId);
        row.AppliedAtUtc.ShouldBe(appliedAt);
        row.WorkChangeId.ShouldBe(keptWorkChangeId);
    }

    [Test]
    public async Task SoftDeleteScenarioData_RemovesOnlyProposedRows_AndKeepsRecordedAnswers()
    {
        var proposed = await AddRowAsync(CandidateId, ReplacementRequestOutcome.Proposed);
        var declined = await AddRowAsync(AlternativeId, ReplacementRequestOutcome.Declined);
        var otherScenario = await AddRowAsync(CandidateId, ReplacementRequestOutcome.Proposed, token: Guid.NewGuid());

        await _service.SoftDeleteScenarioDataAsync(_token, CancellationToken.None);
        await _context.SaveChangesAsync();

        var live = await _context.ReplacementRequests.Select(r => r.Id).ToListAsync();
        live.ShouldNotContain(proposed);
        live.ShouldContain(declined);
        live.ShouldContain(otherScenario);
    }

    private async Task<Guid> SeedScenarioReplacementAsync()
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = AbsentId,
            ShiftId = CloneShiftId,
            CurrentDate = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            AnalyseToken = _token
        };
        var change = new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Type = WorkChangeType.ReplacementWithin,
            ReplaceClientId = CandidateId,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            AnalyseToken = _token
        };
        var cloneShift = new Shift
        {
            Id = CloneShiftId,
            Name = "Clone",
            AnalyseToken = _token,
            ScenarioSourceShiftId = SourceShiftId
        };

        _context.Shift.Add(cloneShift);
        _context.Work.Add(work);
        _context.WorkChange.Add(change);
        await _context.SaveChangesAsync();

        return change.Id;
    }

    private async Task<Guid> AddRowAsync(
        Guid candidateId,
        ReplacementRequestOutcome outcome,
        DateTime? appliedAtUtc = null,
        Guid? workChangeId = null,
        Guid? token = null)
    {
        var row = new ReplacementRequest
        {
            Id = Guid.NewGuid(),
            AbsentClientId = AbsentId,
            CandidateClientId = candidateId,
            ShiftId = SourceShiftId,
            Date = Day,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            Source = ReplacementRequestSource.RecoveryEngine,
            Outcome = outcome,
            ReportedAtUtc = new DateTime(2026, 3, 10, 5, 0, 0, DateTimeKind.Utc),
            ShiftStartUtc = new DateTime(2026, 3, 10, 7, 0, 0, DateTimeKind.Utc),
            AnalyseToken = token ?? _token,
            AppliedAtUtc = appliedAtUtc,
            WorkChangeId = workChangeId
        };
        _context.ReplacementRequests.Add(row);
        await _context.SaveChangesAsync();

        return row.Id;
    }
}
