// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WizardRunCaptureMeasurementRepositoryTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly DateOnly PeriodFrom = new(2026, 4, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 4, 30);
    private static readonly DateOnly Day = new(2026, 4, 10);

    private DataBaseContext _context = null!;
    private WizardRunCaptureRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new WizardRunCaptureRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    private static WizardRunCapture NewCapture(
        Guid? groupId = null, CaptureOutcome? outcome = null, DateTime? measuredAt = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Engine = WizardEngine.TokenEvolution,
            ApplyKind = WizardApplyKind.Direct,
            GroupId = groupId,
            PeriodFrom = PeriodFrom,
            PeriodUntil = PeriodUntil,
            SubScoreJson = "{}",
            Outcome = outcome,
            MeasuredAt = measuredAt,
        };

    private async Task<Guid> AddWorkAsync(Guid agentId, DateOnly date, bool isDeleted)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = agentId,
            ShiftId = ShiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            IsDeleted = isDeleted,
        };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        return work.Id;
    }

    private async Task AddBreakAsync(Guid agentId, DateOnly date)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = agentId,
            AbsenceId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddRecoveryWorkChangeAsync(Guid parentWorkId, Guid coveringAgentId)
    {
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = parentWorkId,
            Type = WorkChangeType.ReplacementWithin,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ReplaceClientId = coveringAgentId,
            Description = RecoveryMarkers.WorkChangeSource,
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddManualWorkChangeAsync(Guid parentWorkId, Guid coveringAgentId)
    {
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = parentWorkId,
            Type = WorkChangeType.ReplacementWithin,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            ReplaceClientId = coveringAgentId,
            Description = "manual planner replacement",
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddGroupItemAsync(
        Guid clientId, Guid groupId, DateTime? validFrom = null, DateTime? validUntil = null)
    {
        _context.GroupItem.Add(new GroupItem
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            GroupId = groupId,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task LoadMeasurementDataAsync_ReturnsSoftDeletedProposalWork()
    {
        var deletedWorkId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { deletedWorkId });

        var data = await _sut.LoadMeasurementDataAsync(capture, RecoveryMarkers.WorkChangeSource);

        data.ProposalCells.Count.ShouldBe(1);
        data.ProposalCells[0].IsDeleted.ShouldBeTrue();
        data.EventCells.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadMeasurementDataAsync_PostApplyBreak_ProducesEventCell()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        await Task.Delay(40);
        await AddBreakAsync(AgentA, Day);

        var data = await _sut.LoadMeasurementDataAsync(capture, RecoveryMarkers.WorkChangeSource);

        data.EventCells.ShouldContain((AgentA, Day));
    }

    [Test]
    public async Task LoadMeasurementDataAsync_RecoveryMarkedWorkChange_ProducesEventCell()
    {
        var proposalWorkId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var parentWorkId = await AddWorkAsync(AgentA, Day, isDeleted: false);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { proposalWorkId });

        await Task.Delay(40);
        await AddRecoveryWorkChangeAsync(parentWorkId, AgentB);

        var data = await _sut.LoadMeasurementDataAsync(capture, RecoveryMarkers.WorkChangeSource);

        data.EventCells.ShouldContain((AgentA, Day));
        data.EventCells.ShouldContain((AgentB, Day));
    }

    [Test]
    public async Task LoadMeasurementDataAsync_PreApplyBreak_IsNotAnEvent()
    {
        await AddBreakAsync(AgentA, Day);
        await Task.Delay(40);

        var workId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        var data = await _sut.LoadMeasurementDataAsync(capture, RecoveryMarkers.WorkChangeSource);

        data.EventCells.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadMeasurementDataAsync_PostApplyManualWorkChange_MarksCellOverlaid()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: false);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        await Task.Delay(40);
        await AddManualWorkChangeAsync(workId, AgentB);

        var data = await _sut.LoadMeasurementDataAsync(capture, RecoveryMarkers.WorkChangeSource);

        data.ProposalCells.Count.ShouldBe(1);
        data.ProposalCells[0].IsDeleted.ShouldBeFalse();
        data.ProposalCells[0].IsOverlaid.ShouldBeTrue();
        data.EventCells.ShouldBeEmpty();
    }

    [Test]
    public async Task LoadMeasurementDataAsync_PreApplyManualWorkChange_IsNotOverlaid()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: false);
        await AddManualWorkChangeAsync(workId, AgentB);
        await Task.Delay(40);

        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        var data = await _sut.LoadMeasurementDataAsync(capture, RecoveryMarkers.WorkChangeSource);

        data.ProposalCells[0].IsOverlaid.ShouldBeFalse();
    }

    [Test]
    public async Task Measurement_ManualWorkChangeOverlay_YieldsCorrectionChurn()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: false);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        await Task.Delay(40);
        await AddManualWorkChangeAsync(workId, AgentB);

        var service = new WizardRunCaptureMeasurementService(
            _sut, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
        await service.MeasureAsync(capture, CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.CorrectionChurn.ShouldBe(1.0);
        stored.EventChurn.ShouldBe(0.0);
    }

    [Test]
    public async Task Measurement_RecoveryWorkChangeOverlay_YieldsEventChurnNotCorrection()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        await Task.Delay(40);
        await AddRecoveryWorkChangeAsync(workId, AgentB);

        var service = new WizardRunCaptureMeasurementService(
            _sut, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
        await service.MeasureAsync(capture, CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.CorrectionChurn.ShouldBe(0.0);
        stored.EventChurn.ShouldBe(1.0);
    }

    [Test]
    public async Task Measurement_DeletedAndManuallyOverlaid_CountsOnceAsCorrection()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        await Task.Delay(40);
        await AddManualWorkChangeAsync(workId, AgentB);

        var service = new WizardRunCaptureMeasurementService(
            _sut, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
        await service.MeasureAsync(capture, CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.CorrectionChurn.ShouldBe(1.0);
        stored.EventChurn.ShouldBe(0.0);
    }

    [Test]
    public async Task GetUnmeasuredForSealAsync_GroupScopedSeal_RecoversDirectApplyCaptureOfGroupClients()
    {
        var groupId = Guid.NewGuid();
        await AddGroupItemAsync(AgentA, groupId);
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: false);
        var directApply = NewCapture(groupId: null);
        await _sut.AddAsync(directApply, new[] { workId });

        var found = await _sut.GetUnmeasuredForSealAsync(PeriodFrom, PeriodUntil, groupId);

        found.Select(c => c.Id).ShouldBe(new[] { directApply.Id });

        var service = new WizardRunCaptureMeasurementService(
            _sut, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
        await service.MeasureAsync(found.Single(), CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == directApply.Id);
        stored.Outcome.ShouldBe(CaptureOutcome.Accepted);
        stored.MeasuredAt.ShouldNotBeNull();
    }

    [Test]
    public async Task GetUnmeasuredForSealAsync_GroupScopedSeal_IgnoresDirectApplyCaptureOfOtherGroupClients()
    {
        var sealedGroup = Guid.NewGuid();
        var otherGroup = Guid.NewGuid();
        await AddGroupItemAsync(AgentA, otherGroup);
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: false);
        var directApply = NewCapture(groupId: null);
        await _sut.AddAsync(directApply, new[] { workId });

        var found = await _sut.GetUnmeasuredForSealAsync(PeriodFrom, PeriodUntil, sealedGroup);

        found.ShouldBeEmpty();
    }

    [Test]
    public async Task SetMeasurementAsync_DoesNotOverwriteAlreadyMeasuredCapture()
    {
        var capture = NewCapture(measuredAt: new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc));
        capture.CorrectionChurn = 0.2;
        capture.EventChurn = 0.0;
        capture.Outcome = CaptureOutcome.Accepted;
        await _sut.AddAsync(capture, Array.Empty<Guid>());

        await _sut.SetMeasurementAsync(capture.Id, 0.9, 0.5, DateTime.UtcNow, CaptureOutcome.Expired);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.CorrectionChurn.ShouldBe(0.2);
        stored.EventChurn.ShouldBe(0.0);
        stored.Outcome.ShouldBe(CaptureOutcome.Accepted);
    }

    [Test]
    public async Task Measurement_SoftDeletedWithoutEvent_YieldsCorrectionChurn()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        var service = new WizardRunCaptureMeasurementService(
            _sut, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
        await service.MeasureAsync(capture, CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.CorrectionChurn.ShouldBe(1.0);
        stored.EventChurn.ShouldBe(0.0);
        stored.Outcome.ShouldBe(CaptureOutcome.Accepted);
        stored.MeasuredAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Measurement_SoftDeletedExplainedByBreak_YieldsEventChurnNotCorrection()
    {
        var workId = await AddWorkAsync(AgentA, Day, isDeleted: true);
        var capture = NewCapture();
        await _sut.AddAsync(capture, new[] { workId });

        await Task.Delay(40);
        await AddBreakAsync(AgentA, Day);

        var service = new WizardRunCaptureMeasurementService(
            _sut, Substitute.For<ILogger<WizardRunCaptureMeasurementService>>());
        await service.MeasureAsync(capture, CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.CorrectionChurn.ShouldBe(0.0);
        stored.EventChurn.ShouldBe(1.0);
    }

    [Test]
    public async Task GetUnmeasuredForSealAsync_ReturnsOverlappingUnmeasured_ExcludesRejectedAndMeasured()
    {
        var groupId = Guid.NewGuid();
        var pending = NewCapture(groupId);
        var rejected = NewCapture(groupId, outcome: CaptureOutcome.Rejected);
        var measured = NewCapture(groupId, measuredAt: DateTime.UtcNow);
        await _sut.AddAsync(pending, Array.Empty<Guid>());
        await _sut.AddAsync(rejected, Array.Empty<Guid>());
        await _sut.AddAsync(measured, Array.Empty<Guid>());

        var found = await _sut.GetUnmeasuredForSealAsync(PeriodFrom, PeriodUntil, groupId);

        found.Select(c => c.Id).ShouldBe(new[] { pending.Id });
    }

    [Test]
    public async Task GetUnmeasuredForSealAsync_NullGroup_MatchesAllGroups()
    {
        var a = NewCapture(Guid.NewGuid());
        var b = NewCapture(null);
        await _sut.AddAsync(a, Array.Empty<Guid>());
        await _sut.AddAsync(b, Array.Empty<Guid>());

        var found = await _sut.GetUnmeasuredForSealAsync(PeriodFrom, PeriodUntil, null);

        found.Select(c => c.Id).ShouldBe(new[] { a.Id, b.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task GetUnmeasuredExpiredAsync_ReturnsOnlyCapturesEndedBeforeCutoff()
    {
        var oldCapture = NewCapture();
        await _sut.AddAsync(oldCapture, Array.Empty<Guid>());
        var recentCapture = new WizardRunCapture
        {
            Id = Guid.NewGuid(),
            Engine = WizardEngine.TokenEvolution,
            ApplyKind = WizardApplyKind.Direct,
            PeriodFrom = new DateOnly(2026, 6, 1),
            PeriodUntil = new DateOnly(2026, 6, 30),
            SubScoreJson = "{}",
        };
        await _sut.AddAsync(recentCapture, Array.Empty<Guid>());

        var found = await _sut.GetUnmeasuredExpiredAsync(new DateOnly(2026, 5, 15));

        found.Select(c => c.Id).ShouldBe(new[] { oldCapture.Id });
    }

    [Test]
    public async Task SetMeasurementAsync_DoesNotOverwriteRejectedCapture()
    {
        var capture = NewCapture(outcome: CaptureOutcome.Rejected);
        await _sut.AddAsync(capture, Array.Empty<Guid>());

        await _sut.SetMeasurementAsync(capture.Id, 0.9, 0.1, DateTime.UtcNow, CaptureOutcome.Accepted);

        var stored = await _context.WizardRunCapture.SingleAsync(c => c.Id == capture.Id);
        stored.Outcome.ShouldBe(CaptureOutcome.Rejected);
        stored.CorrectionChurn.ShouldBeNull();
        stored.MeasuredAt.ShouldBeNull();
    }
}
