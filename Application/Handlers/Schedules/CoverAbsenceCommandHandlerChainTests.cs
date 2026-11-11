// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the repair rounds, the candidate pool and the swap chains of CoverAbsenceCommandHandler: a cover the
/// compliance partition refuses is repaired again without the refused assignment (bounded number of rounds), a slot no
/// later round covers stays reported as blocked, candidates the caller cannot see never reach the engine, the first
/// movable day is today of the company, the further moves of a chain are reported on the covered slot and the scenario
/// is widened to every day a written move touches. The engine is the real one behind a recording substitute unless a
/// test scripts its proposal; the CompliancePartitionService is real over a substituted conflict checker that refuses
/// the plans of chosen employees; the materializer is substituted.
/// </summary>

using Klacks.Api.Application.Commands.Breaks;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.ScheduleRecovery.Engine;
using Klacks.UnitTest.ScheduleRecovery;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CoverRun = (Klacks.Api.Application.DTOs.Schedules.CoverAbsenceOutcome Outcome, Klacks.ScheduleRecovery.Model.RecoverySnapshot Snapshot, System.Collections.Generic.IReadOnlyList<(System.DateOnly Date, System.Guid? WorkId, System.DateTime? ShiftStartUtc, System.Guid? BreakId)> AbsenceDays);
using Rec = Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class CoverAbsenceCommandHandlerChainTests
{
    private const int ExpectedCoverRounds = 4;
    private const string BlockedReason = "blocked";
    private const string NoCandidateReason = "no eligible candidate";
    private const string RecalculationFailedReason = "recalculation failed";
    private const string ScenarioName = "cover";
    private const string RestViolation = "schedule.error-list.rest-violation";
    private const string ZurichTimeZoneId = "Europe/Zurich";

    private static readonly Guid Absent = RecoveryTestKit.Agent(1);
    private static readonly Guid Bob = RecoveryTestKit.Agent(2);
    private static readonly Guid Carla = RecoveryTestKit.Agent(3);
    private static readonly Guid Dave = RecoveryTestKit.Agent(4);
    private static readonly Guid Erin = RecoveryTestKit.Agent(5);
    private static readonly Guid Finn = RecoveryTestKit.Agent(6);
    private static readonly Guid ShiftId = RecoveryTestKit.Shift(1);
    private static readonly Guid EarlyShiftId = RecoveryTestKit.Shift(2);
    private static readonly Guid OtherShiftId = RecoveryTestKit.Shift(3);
    private static readonly Guid EveningShiftId = RecoveryTestKit.Shift(4);
    private static readonly Guid GroupId = RecoveryTestKit.Group(1);
    private static readonly Guid AbsenceId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid ClonedWorkId = Guid.NewGuid();
    private static readonly DateOnly Date = new(2026, 3, 10);
    private static readonly DateTimeOffset LateOnTheAbsenceDay = new(2026, 3, 10, 23, 0, 0, TimeSpan.Zero);

    private IAnalyseScenarioRepository _scenarioRepo = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IScheduleEntriesService _scheduleEntries = null!;
    private IRecoverySnapshotBuilder _snapshotBuilder = null!;
    private IRecoveryEngine _engine = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private IMediator _mediator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IClientVisibilityGuard _visibilityGuard = null!;
    private IGroupVisibilityGuard _groupVisibilityGuard = null!;
    private IReplacementRequestRecorder _recorder = null!;
    private IAbsenceCoverMaterializer _materializer = null!;
    private CoverAbsenceCommandHandler _handler = null!;

    private readonly HashSet<Guid> _refusedClients = [];
    private readonly List<(DateOnly From, DateOnly Until)> _flushedRanges = [];
    private AnalyseScenario? _scenario;
    private bool _materializerHoldsEverything;

    [SetUp]
    public void Setup()
    {
        _refusedClients.Clear();
        _flushedRanges.Clear();
        _scenario = null;
        _materializerHoldsEverything = false;

        _scenarioRepo = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepo.When(repository => repository.Add(Arg.Any<AnalyseScenario>()))
            .Do(call => _scenario = call.Arg<AnalyseScenario>());

        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _scenarioService.CloneScenarioDataWithMapsAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns((new Dictionary<Guid, Guid>(), new Dictionary<Guid, Guid> { [WorkId] = ClonedWorkId }));

        _scheduleEntries = Substitute.For<IScheduleEntriesService>();
        ScheduleEntriesStub.Serve(_scheduleEntries, [AbsentWorkCell()]);

        _snapshotBuilder = Substitute.For<IRecoverySnapshotBuilder>();

        var realEngine = new LocalRepairEngine();
        _engine = Substitute.For<IRecoveryEngine>();
        _engine.Repair(Arg.Any<Rec.RecoverySnapshot>(), Arg.Any<Rec.AbsenceEvent>(), Arg.Any<Rec.Ruleset>())
            .Returns(call => realEngine.Repair(
                call.Arg<Rec.RecoverySnapshot>(), call.Arg<Rec.AbsenceEvent>(), call.Arg<Rec.Ruleset>()));

        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _conflictChecker.CheckAsync(
                Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<IReadOnlyList<PlannedRemovalRow>>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => RefusalsFor(call.Arg<IReadOnlyList<PlannedWorkRow>>()));

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<BulkAddBreaksCommand>(), Arg.Any<CancellationToken>())
            .Returns(new BulkBreaksResponse());

        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<CoverRun>>>())
            .Returns(call => call.Arg<Func<Task<CoverRun>>>()());
        _unitOfWork.When(unitOfWork => unitOfWork.CompleteAsync())
            .Do(_ => _flushedRanges.Add((_scenario!.FromDate, _scenario.UntilDate)));

        _visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _visibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        SetVisibleAgents(_ => true);
        _groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupVisibilityGuard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _recorder = Substitute.For<IReplacementRequestRecorder>();
        _recorder.RecordProposalsAsync(
                Arg.Any<ReplacementProposalContext>(), Arg.Any<IReadOnlyList<CoveredSlot>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<CoveredSlot>>(1));

        _materializer = Substitute.For<IAbsenceCoverMaterializer>();
        _materializer.FindSourceWorksAsync(
                Arg.Any<IReadOnlyDictionary<Guid, Guid>>(), Arg.Any<Guid>(), Arg.Any<CoverReport>(), Arg.Any<CancellationToken>())
            .Returns(new CoverSourceWorks(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>()));
        _materializer.MaterialiseAsync(Arg.Any<CoverMaterialisation>(), Arg.Any<CancellationToken>())
            .Returns(call => new CoverMaterialisationResult(
                new HashSet<Guid>(),
                [],
                _materializerHoldsEverything ? call.Arg<CoverMaterialisation>().Deltas : []));

        _handler = NewHandler(new FixedCompanyClock(LateOnTheAbsenceDay, TimeZoneInfo.Utc));
    }

    private CoverAbsenceCommandHandler NewHandler(ICompanyClock clock)
    {
        var nameGenerator = Substitute.For<IScenarioNameGenerator>();
        nameGenerator.GenerateAsync(
                Arg.Any<ScenarioNameKind>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ScenarioName);
        var partitionService = new CompliancePartitionService(
            _conflictChecker,
            Substitute.For<ISupervisorOverrideAuthorizer>(),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<ILogger<CompliancePartitionService>>());
        return new CoverAbsenceCommandHandler(
            _scenarioRepo, _scenarioService, _scheduleEntries, _snapshotBuilder, _engine,
            partitionService, _mediator, _unitOfWork, Substitute.For<IEscalationChainService>(), clock, _visibilityGuard,
            _groupVisibilityGuard, nameGenerator,
            _recorder, _materializer, NullLogger<CoverAbsenceCommandHandler>.Instance);
    }

    private PreCommitCheckResult RefusalsFor(IReadOnlyList<PlannedWorkRow> rows)
        => new(rows
            .Where(row => _refusedClients.Contains(row.ClientId))
            .Select(row => new ScheduleValidationNotificationDto
            {
                Type = ScheduleValidationType.Error,
                ClientId = row.ClientId,
                Date = row.Date,
                Comment = RestViolation,
            })
            .ToList());

    private void SetVisibleAgents(Func<Guid, bool> isVisible)
        => _visibilityGuard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0).Where(isVisible).ToList());

    private void UseSnapshot(Rec.RecoverySnapshot snapshot)
        => _snapshotBuilder.BuildAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyList<DateOnly>>(), Arg.Any<ScenarioSourceScope?>(),
                Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<IReadOnlySet<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(snapshot);

    private void UseProposal(params Rec.CellDelta[] deltas)
        => _engine.Repair(Arg.Any<Rec.RecoverySnapshot>(), Arg.Any<Rec.AbsenceEvent>(), Arg.Any<Rec.Ruleset>())
            .Returns(new Rec.RecoveryProposal(deltas, [], [], Rec.RecoveryObjective.Zero, Rec.EscalationTier.InGroupSwap));

    private IReadOnlyList<Rec.RecoverySnapshot> RepairSnapshots()
        => _engine.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IRecoveryEngine.Repair))
            .Select(call => (Rec.RecoverySnapshot)call.GetArguments()[0]!)
            .ToList();

    private static Rec.RecoverySnapshot WithFreeCandidates(params (Guid Id, string Name)[] candidates)
    {
        var builder = new SnapshotBuilder()
            .Days(Date)
            .Agent(Absent, "Absent")
            .Work(Absent, Date, ShiftId, Rec.ShiftCategory.Early, At(Date, 8), At(Date, 16), 8m, false, WorkId);
        foreach (var (id, name) in candidates)
        {
            builder.Agent(id, name);
        }
        return builder.Build();
    }

    private static Rec.RecoverySnapshot WithNamedAgents()
        => WithFreeCandidates((Bob, "Bob"), (Carla, "Carla"), (Dave, "Dave"), (Erin, "Erin"));

    private static DateTime At(DateOnly date, int hour) => date.ToDateTime(new TimeOnly(hour, 0));

    private static Rec.CellDelta Move(
        Guid shiftId, DateOnly date, Guid from, Guid to, Rec.EscalationTier tier, int startHour, int endHour,
        int optionId = 0, params Guid[] workIds)
        => new(
            shiftId, date, from, to, Rec.ShiftCategory.Early, At(date, startHour), At(date, endHour),
            endHour - startHour, tier, workIds, optionId);

    private static ScheduleCell AbsentWorkCell() => new()
    {
        EntryType = (int)ScheduleEntryType.Work,
        ClientId = Absent,
        SourceId = WorkId,
        EntryId = ShiftId,
        EntryDate = Date.ToDateTime(TimeOnly.MinValue),
        StartTime = new TimeSpan(8, 0, 0),
        EndTime = new TimeSpan(16, 0, 0),
        LockLevel = 0
    };

    private static CoverAbsenceCommand Command()
        => new(Absent, Date, GroupId, AbsenceId, NotifyEscalationRoster: false, Source: ReplacementRequestSource.PlannerDialog);

    private Task<CoverAbsenceOutcome> Cover() => _handler.Handle(Command(), CancellationToken.None);

    [Test]
    public async Task AnAcceptedCover_IsRepairedOnce()
    {
        // Arrange
        UseSnapshot(WithFreeCandidates((Bob, "Bob"), (Carla, "Carla")));

        // Act
        var outcome = await Cover();

        // Assert
        RepairSnapshots().Count.ShouldBe(1);
        outcome.Covered.ShouldHaveSingleItem().ReplacementClientId.ShouldBe(Bob);
        outcome.Uncovered.ShouldBeEmpty();
    }

    [Test]
    public async Task ARefusedCover_IsRepairedAgain_WithoutTheRefusedAssignment_AndTheNextCandidateCovers()
    {
        // Arrange: Bob and Carla are both free and Bob wins the tie; the compliance check refuses every plan with Bob.
        UseSnapshot(WithFreeCandidates((Bob, "Bob"), (Carla, "Carla")));
        _refusedClients.Add(Bob);

        // Act
        var outcome = await Cover();

        // Assert: the second repair got the same plan with exactly Bob's refused assignment gated.
        var snapshots = RepairSnapshots();
        snapshots.Count.ShouldBe(2);
        snapshots[0].IsIneligible(Bob, ShiftId, Date).ShouldBeFalse();
        snapshots[1].IsIneligible(Bob, ShiftId, Date).ShouldBeTrue();
        snapshots[1].IsIneligible(Carla, ShiftId, Date).ShouldBeFalse();
        snapshots[1].IsIneligible(Bob, EarlyShiftId, Date).ShouldBeFalse();
        snapshots[1].IsIneligible(Bob, ShiftId, Date.AddDays(1)).ShouldBeFalse();
        snapshots[1].Agents.Select(a => a.Id).ShouldBe([Absent, Bob, Carla]);
        snapshots[1].FirstMovableDay.ShouldBe(Date);

        var slot = outcome.Covered.ShouldHaveSingleItem();
        slot.ReplacementClientId.ShouldBe(Carla);
        slot.ReplacementName.ShouldBe("Carla");
        outcome.Uncovered.ShouldBeEmpty();
        await _materializer.Received(1).MaterialiseAsync(
            Arg.Is<CoverMaterialisation>(m => m.Deltas.Count == 1 && m.Deltas[0].ToAgentId == Carla),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheRepair_StopsAfterTheLastRound_WhenEveryRoundBringsANewRefusedCover()
    {
        // Arrange: five free candidates, and the compliance check refuses a plan with any of them.
        Guid[] candidates = [Bob, Carla, Dave, Erin, Finn];
        UseSnapshot(WithFreeCandidates((Bob, "Bob"), (Carla, "Carla"), (Dave, "Dave"), (Erin, "Erin"), (Finn, "Finn")));
        _refusedClients.UnionWith(candidates);

        // Act
        var outcome = await Cover();

        // Assert: every round gates one more candidate; the fifth candidate is never tried.
        var snapshots = RepairSnapshots();
        snapshots.Count.ShouldBe(ExpectedCoverRounds);
        snapshots
            .Select(snapshot => candidates.Count(id => snapshot.IsIneligible(id, ShiftId, Date)))
            .ShouldBe([0, 1, 2, 3]);
        outcome.Covered.ShouldBeEmpty();
        var open = outcome.Uncovered.ShouldHaveSingleItem();
        open.Reason.ShouldBe(BlockedReason);
        open.ShiftId.ShouldBe(ShiftId);
        open.Date.ShouldBe(Date);
        await _materializer.Received(1).MaterialiseAsync(
            Arg.Is<CoverMaterialisation>(m => m.Deltas.Count == 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASlotNoLaterRoundCovers_StaysReportedAsBlocked()
    {
        // Arrange: Bob is the only candidate and the compliance check refuses him, so the second repair finds nobody.
        UseSnapshot(WithFreeCandidates((Bob, "Bob")));
        _refusedClients.Add(Bob);

        // Act
        var outcome = await Cover();

        // Assert
        RepairSnapshots().Count.ShouldBe(2);
        outcome.Covered.ShouldBeEmpty();
        var open = outcome.Uncovered.ShouldHaveSingleItem();
        open.Reason.ShouldBe(BlockedReason);
        open.ShiftId.ShouldBe(ShiftId);
        open.Date.ShouldBe(Date);
        open.WorkId.ShouldBe(ClonedWorkId);
        open.StartTime.ShouldBe(new TimeOnly(8, 0));
        open.EndTime.ShouldBe(new TimeOnly(16, 0));
    }

    [Test]
    public async Task OfTwoWorksOfOneShiftOnOneDay_OnlyTheOneWithARefusedCover_IsReportedAsBlocked()
    {
        // Arrange: the absence leaves a morning and an afternoon work of the same shift. Bob can only take the morning,
        // a locked work of his own fills the afternoon, and the compliance check refuses him.
        var afternoonWorkId = Guid.NewGuid();
        var clonedAfternoonWorkId = Guid.NewGuid();
        _scenarioService.CloneScenarioDataWithMapsAsync(
                Arg.Any<Guid?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns((
                new Dictionary<Guid, Guid>(),
                new Dictionary<Guid, Guid> { [WorkId] = ClonedWorkId, [afternoonWorkId] = clonedAfternoonWorkId }));
        UseSnapshot(new SnapshotBuilder()
            .Days(Date)
            .Agent(Absent, "Absent")
            .Agent(Bob, "Bob")
            .Work(Absent, Date, ShiftId, Rec.ShiftCategory.Early, At(Date, 8), At(Date, 12), 4m, false, WorkId)
            .Work(Absent, Date, ShiftId, Rec.ShiftCategory.Late, At(Date, 14), At(Date, 18), 4m, false, afternoonWorkId)
            .Work(Bob, Date, OtherShiftId, Rec.ShiftCategory.Late, At(Date, 14), At(Date, 18), 4m, true)
            .Build());
        _refusedClients.Add(Bob);

        // Act
        var outcome = await Cover();

        // Assert: the morning had a cover the check refused, the afternoon never had a candidate.
        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.Count.ShouldBe(2);
        outcome.Uncovered.Single(slot => slot.WorkId == ClonedWorkId).Reason.ShouldBe(BlockedReason);
        outcome.Uncovered.Single(slot => slot.WorkId == clonedAfternoonWorkId).Reason.ShouldBe(NoCandidateReason);
    }

    [Test]
    public async Task ACandidateTheCallerCannotSee_IsNotInThePoolOfTheRepair_AndItsNameStaysResolvable()
    {
        // Arrange: Bob would win the tie, but the caller cannot see him.
        UseSnapshot(WithFreeCandidates((Bob, "Bob"), (Carla, "Carla")));
        SetVisibleAgents(id => id != Bob);

        // Act
        var outcome = await Cover();

        // Assert
        var snapshot = RepairSnapshots().ShouldHaveSingleItem();
        snapshot.Agents.Select(a => a.Id).ShouldBe([Absent, Carla]);
        snapshot.FindAgent(Bob).ShouldNotBeNull().DisplayName.ShouldBe("Bob");
        outcome.Covered.ShouldHaveSingleItem().ReplacementClientId.ShouldBe(Carla);
        outcome.Uncovered.ShouldBeEmpty();
        await _visibilityGuard.Received(1).FilterVisibleAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(Bob) && ids.Contains(Carla)),
            Arg.Any<Func<Guid, Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheFirstMovableDay_IsTodayInTheCompanyTimeZone()
    {
        // Arrange: 23:30 UTC on the 9th is already the 10th in Zurich.
        var clock = new FixedCompanyClock(
            new DateTimeOffset(2026, 3, 9, 23, 30, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById(ZurichTimeZoneId));
        var handler = NewHandler(clock);
        UseSnapshot(WithFreeCandidates((Bob, "Bob")));

        // Act
        await handler.Handle(Command(), CancellationToken.None);

        // Assert
        RepairSnapshots().ShouldHaveSingleItem().FirstMovableDay.ShouldBe(new DateOnly(2026, 3, 10));
    }

    [Test]
    public async Task TheFurtherMovesOfAChain_AreReportedOnTheCoveredSlot_FromTheCoverOutwards()
    {
        // Arrange: the engine lists a chain deepest move first. Bob covers and hands his early of the next day to Carla,
        // who hands her work of the day after to the borrowed Dave. A second work of the absence is covered directly.
        var nextDay = Date.AddDays(1);
        var dayAfter = Date.AddDays(2);
        UseSnapshot(WithNamedAgents());
        UseProposal(
            Move(OtherShiftId, dayAfter, Carla, Dave, Rec.EscalationTier.CrossGroupSwap, 14, 22),
            Move(EarlyShiftId, nextDay, Bob, Carla, Rec.EscalationTier.InGroupSwap, 6, 14),
            Move(ShiftId, Date, Absent, Bob, Rec.EscalationTier.InGroupSwap, 8, 16, 0, WorkId),
            Move(EveningShiftId, Date, Absent, Erin, Rec.EscalationTier.InGroupFree, 18, 20, optionId: 1));

        // Act
        var outcome = await Cover();

        // Assert
        outcome.Covered.Count.ShouldBe(2);
        var chained = outcome.Covered.Single(slot => slot.ShiftId == ShiftId);
        chained.ReplacementClientId.ShouldBe(Bob);
        chained.Relocations.ShouldNotBeNull().ShouldBe(new[]
        {
            new CoverRelocation(
                EarlyShiftId, nextDay, Bob, "Bob", Carla, "Carla", new TimeOnly(6, 0), new TimeOnly(14, 0),
                (int)Rec.EscalationTier.InGroupSwap),
            new CoverRelocation(
                OtherShiftId, dayAfter, Carla, "Carla", Dave, "Dave", new TimeOnly(14, 0), new TimeOnly(22, 0),
                (int)Rec.EscalationTier.CrossGroupSwap),
        });
        var direct = outcome.Covered.Single(slot => slot.ShiftId == EveningShiftId);
        direct.ReplacementClientId.ShouldBe(Erin);
        direct.Relocations.ShouldNotBeNull().ShouldBeEmpty();
        await _recorder.Received(1).RecordProposalsAsync(
            Arg.Any<ReplacementProposalContext>(),
            Arg.Is<IReadOnlyList<CoveredSlot>>(slots =>
                slots.Count == 2 && slots.Single(slot => slot.ShiftId == ShiftId).Relocations!.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADirectCover_HasNoRelocations()
    {
        // Arrange
        UseSnapshot(WithFreeCandidates((Bob, "Bob")));

        // Act
        var outcome = await Cover();

        // Assert
        outcome.Covered.ShouldHaveSingleItem().Relocations.ShouldNotBeNull().ShouldBeEmpty();
    }

    [TestCase(3)]
    [TestCase(-2)]
    public async Task AWrittenMoveOnAnotherDay_WidensTheScenarioToThatDay(int dayOffset)
    {
        // Arrange: Bob covers on the absence day and hands a work of another day to Carla.
        var otherDay = Date.AddDays(dayOffset);
        var expectedFrom = dayOffset < 0 ? otherDay : Date;
        var expectedUntil = dayOffset > 0 ? otherDay : Date;
        UseSnapshot(WithNamedAgents());
        UseProposal(
            Move(EarlyShiftId, otherDay, Bob, Carla, Rec.EscalationTier.InGroupSwap, 6, 14),
            Move(ShiftId, Date, Absent, Bob, Rec.EscalationTier.InGroupSwap, 8, 16, 0, WorkId));

        // Act
        await Cover();

        // Assert: the plan was cloned for the absence day and the widened range is what the last flush saved.
        _scenario.ShouldNotBeNull();
        _scenario.FromDate.ShouldBe(expectedFrom);
        _scenario.UntilDate.ShouldBe(expectedUntil);
        _flushedRanges[0].ShouldBe((Date, Date));
        _flushedRanges[^1].ShouldBe((expectedFrom, expectedUntil));
    }

    [Test]
    public async Task ACoverWithoutAMoveOnAnotherDay_LeavesTheScenarioOnTheAbsenceDays()
    {
        // Arrange
        UseSnapshot(WithFreeCandidates((Bob, "Bob")));

        // Act
        var outcome = await Cover();

        // Assert
        outcome.Covered.ShouldHaveSingleItem();
        _scenario.ShouldNotBeNull();
        _scenario.FromDate.ShouldBe(Date);
        _scenario.UntilDate.ShouldBe(Date);
        _flushedRanges[^1].ShouldBe((Date, Date));
    }

    [Test]
    public async Task AChainThatWasNotWritten_DoesNotWidenTheScenario()
    {
        // Arrange: the chain reaches three days ahead, but the materializer holds all of its moves back.
        UseSnapshot(WithNamedAgents());
        UseProposal(
            Move(EarlyShiftId, Date.AddDays(3), Bob, Carla, Rec.EscalationTier.InGroupSwap, 6, 14),
            Move(ShiftId, Date, Absent, Bob, Rec.EscalationTier.InGroupSwap, 8, 16, 0, WorkId));
        _materializerHoldsEverything = true;

        // Act
        var outcome = await Cover();

        // Assert
        outcome.Covered.ShouldBeEmpty();
        outcome.Uncovered.ShouldHaveSingleItem().Reason.ShouldBe(RecalculationFailedReason);
        _scenario.ShouldNotBeNull();
        _scenario.FromDate.ShouldBe(Date);
        _scenario.UntilDate.ShouldBe(Date);
    }
}
