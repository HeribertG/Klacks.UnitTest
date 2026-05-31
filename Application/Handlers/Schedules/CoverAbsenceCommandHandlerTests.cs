// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CoverAbsenceCommandHandler: it clones + flushes before reading the absent employee's
/// slots, records the absence (Break) in the scenario, writes a Replacement WorkChange for each
/// coverable slot (skipping locked ones and slots with no eligible candidate via FindReplacementQuery),
/// and reports covered/uncovered. All downstream work goes through IMediator.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class CoverAbsenceCommandHandlerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 10);

    private IAnalyseScenarioRepository _scenarioRepo = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IScheduleEntriesService _scheduleEntries = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CoverAbsenceCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _scenarioRepo = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepo.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AnalyseScenario>());

        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _scenarioService.CloneScenarioDataAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid>());

        _scheduleEntries = Substitute.For<IScheduleEntriesService>();
        SetSlots();

        _mediator = Substitute.For<IMediator>();
        SetReplacement();
        _mediator.Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkBreaksResponse());
        _mediator.Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkChangeResource());

        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new CoverAbsenceCommandHandler(
            _scenarioRepo, _scenarioService, _scheduleEntries, _mediator, _unitOfWork);
    }

    private void SetSlots(params ScheduleCell[] cells)
        => _scheduleEntries.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ScheduleCell>(cells));

    private void SetReplacement(Guid? candidateId = null)
    {
        var eligible = candidateId.HasValue
            ? new List<ReplacementCandidate> { new(candidateId.Value, "Bob", false, []) }
            : new List<ReplacementCandidate>();
        _mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(eligible, []));
    }

    private static ScheduleCell WorkSlot(int lockLevel = 0) => new()
    {
        EntryType = (int)ScheduleEntryType.Work,
        ClientId = ClientId,
        SourceId = Guid.NewGuid(),
        EntryId = ShiftId,
        EntryDate = Date.ToDateTime(TimeOnly.MinValue),
        StartTime = new TimeSpan(8, 0, 0),
        EndTime = new TimeSpan(16, 0, 0),
        LockLevel = lockLevel
    };

    private Task<CoverAbsenceOutcome> Cover()
        => _handler.Handle(new CoverAbsenceCommand(ClientId, Date, GroupId, AbsenceId), CancellationToken.None);

    [Test]
    public async Task CoveredSlot_WritesReplacementWorkChange_AndRecordsAbsence()
    {
        var candidate = Guid.NewGuid();
        SetSlots(WorkSlot());
        SetReplacement(candidate);

        var outcome = await Cover();

        outcome.Covered.Count.ShouldBe(1);
        outcome.Covered[0].ReplacementClientId.ShouldBe(candidate);
        outcome.Uncovered.ShouldBeEmpty();

        await _scenarioRepo.Received(1).Add(Arg.Any<AnalyseScenario>());
        await _mediator.Received(1).Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c =>
                c.Resource.Type == WorkChangeType.ReplacementWithin && c.Resource.ReplaceClientId == candidate),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LockedSlot_Reported_NotCovered()
    {
        SetSlots(WorkSlot(lockLevel: (int)WorkLockLevel.Confirmed));
        SetReplacement(Guid.NewGuid());

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(1);
        outcome.Uncovered[0].Reason.ShouldBe("locked");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoEligibleCandidate_ReportedAsUnderCoverage()
    {
        SetSlots(WorkSlot());
        SetReplacement(candidateId: null);

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(1);
        outcome.Uncovered[0].Reason.ShouldBe("no eligible candidate");
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoSlots_StillRecordsAbsence()
    {
        SetSlots();

        var outcome = await Cover();

        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.ShouldBeEmpty();
        await _mediator.Received(1).Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ClonesAndFlushes_BeforeReadingSlots()
    {
        var order = new List<string>();
        _scenarioService.When(s => s.CloneScenarioDataAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("clone"));
        _unitOfWork.When(u => u.CompleteAsync()).Do(_ => order.Add("complete"));
        _scheduleEntries.When(s => s.GetScheduleEntriesQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(), Arg.Any<Guid?>()))
            .Do(_ => order.Add("read"));

        await Cover();

        order.IndexOf("clone").ShouldBeLessThan(order.IndexOf("complete"));
        order.IndexOf("complete").ShouldBeLessThan(order.IndexOf("read"));
    }
}
