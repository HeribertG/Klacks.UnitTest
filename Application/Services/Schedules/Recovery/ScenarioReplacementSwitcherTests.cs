// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// An accepted alternative becomes the replacement of exactly its slot in the scenario: the existing replacement
/// WorkChange is rewritten through PutCommand (period hours, locks, notifications), the old stand-in's untouched proposal
/// row, swap relocation and temporary membership go with it, recorded answers stay. Everything else is a no-op. A swap
/// chain is taken back as far as it reaches (works handed on on other days, the works their receivers handed on in turn),
/// a work that went away for an own absence stays, and a chain whose works cannot be told apart refuses the switch. The
/// repository substitutes answer the range reads like the database: only works inside the asked range come back.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;
using RulesetDefaults = Klacks.ScheduleRecovery.Model.RulesetDefaults;

namespace Klacks.UnitTest.Application.Services.Schedules.Recovery;

[TestFixture]
public class ScenarioReplacementSwitcherTests
{
    private static readonly DateOnly Day = new(2026, 12, 15);
    private static readonly TimeOnly Start = new(7, 0);
    private static readonly TimeOnly End = new(15, 0);

    private readonly Guid _token = Guid.NewGuid();
    private readonly Guid _absentId = Guid.NewGuid();
    private readonly Guid _oldStandInId = Guid.NewGuid();
    private readonly Guid _alternativeId = Guid.NewGuid();
    private readonly Guid _rootShiftId = Guid.NewGuid();
    private readonly Guid _cloneShiftId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();

    private IWorkChangeRepository _workChangeRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IAbsenceCoverMaterializer _coverMaterializer = null!;
    private IReplacementRequestRepository _requestRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IMediator _mediator = null!;
    private ScenarioReplacementSwitcher _switcher = null!;
    private AnalyseScenario _scenario = null!;

    private const int Reach = RulesetDefaults.ReliefReachDays;
    private const string ChainRefusal = "swap chain";
    private readonly Dictionary<Guid, List<Work>> _movedFrom = [];
    private readonly List<ReplacementRequest> _book = [];

    [SetUp]
    public void SetUp()
    {
        _movedFrom.Clear();
        _book.Clear();
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.ListMovedByScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());
        _workRepository.ListMovedByScenarioAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Work>());
        _coverMaterializer = Substitute.For<IAbsenceCoverMaterializer>();
        _coverMaterializer.PassOnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MovedWorkHandOverResult.Done);
        _coverMaterializer.ReturnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MovedWorkHandOverResult.Done);
        _requestRepository = Substitute.For<IReplacementRequestRepository>();
        _requestRepository.ListLiveByTokenAsync(_token, Arg.Any<CancellationToken>()).Returns(_book);
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _mediator = Substitute.For<IMediator>();

        _scenario = new AnalyseScenario { Token = _token, GroupId = _groupId, Status = AnalyseScenarioStatus.Active };
        _scenarioRepository.GetByTokenAsync(_token, Arg.Any<CancellationToken>()).Returns(_scenario);
        _scenarioService.ResolveRootShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Guid> { [_cloneShiftId] = _rootShiftId });
        GivenEligible(_alternativeId);
        _mediator.Send(Arg.Any<GetWorkChangeInScopeQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci => new WorkChangeResource { Id = ci.Arg<GetWorkChangeInScopeQuery>().Id, WorkId = Guid.NewGuid() });
        _mediator.Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<PutCommand<WorkChangeResource>>().Resource);
        _mediator.Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns(new WorkChangeResource());
        _workChangeRepository
            .ListScenarioReplacementsAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _workChangeRepository
            .ListReplacementsWrittenByScenarioAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _workChangeRepository
            .ListReplacementsWrittenByScenarioAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _workChangeRepository
            .ListNonReplacementChangesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _switcher = new ScenarioReplacementSwitcher(
            _workChangeRepository, _workRepository, _coverMaterializer, _requestRepository, _scenarioRepository,
            _scenarioService, _mediator);
    }

    [Test]
    public async Task AcceptedAlternative_BecomesTheSlotsReplacement()
    {
        var slot = GivenSlotChange();

        var result = await Apply(Row());

        result.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<PutCommand<WorkChangeResource>>(c => c.Resource.Id == slot.Id && c.Resource.ReplaceClientId == _alternativeId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptedAlternative_TakesOverAMovedWork_AndTheRowBecomesAMove()
    {
        var work = GivenMovedWork(_absentId, _oldStandInId);
        var row = Row();

        (await Apply(row)).ShouldBeTrue();

        await _coverMaterializer.Received(1).PassOnMovedWorkAsync(_token, work.Id, _alternativeId, Arg.Any<CancellationToken>());
        row.Kind.ShouldBe(ReplacementRequestKind.Move);
        row.WorkId.ShouldBe(work.Id);
    }

    [Test]
    public async Task AcceptanceOfTheOwnerOfAMovedWork_ChangesNothing()
    {
        GivenMovedWork(_absentId, _alternativeId);

        (await Apply(Row())).ShouldBeTrue();

        await _coverMaterializer.DidNotReceiveWithAnyArgs().PassOnMovedWorkAsync(default, default, default, default);
    }

    [Test]
    public async Task AMovedWorkOfAnotherShift_IsNotTheSlot()
    {
        GivenMovedWork(_absentId, _oldStandInId, Guid.NewGuid());

        (await Apply(Row())).ShouldBeFalse();

        await _coverMaterializer.DidNotReceiveWithAnyArgs().PassOnMovedWorkAsync(default, default, default, default);
    }

    [Test]
    public async Task AMovedWork_IsRefused_ForAnIneligibleCandidate()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        GivenEligible();

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        await _coverMaterializer.DidNotReceiveWithAnyArgs().PassOnMovedWorkAsync(default, default, default, default);
    }

    [Test]
    public async Task AMovedWork_IsRefused_WhenTheCandidateStandsInAtThatTime()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        _workChangeRepository
            .HasOverlappingScenarioReplacementAsync(_token, _alternativeId, Day, Start, Start, End, Arg.Any<CancellationToken>())
            .Returns(true);

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));
    }

    [Test]
    public async Task AHeldWork_RefusesTheSwitch_WithItsOwnReason()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        _coverMaterializer.PassOnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(MovedWorkHandOverResult.Held);

        var row = Row();
        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(row));

        refused.Message.ShouldContain("sealed or replaced");
        row.Kind.ShouldBe(ReplacementRequestKind.Replacement);
        row.WorkId.ShouldBeNull();
    }

    [Test]
    public async Task AHeldHop_StaysWithoutRefusingTheSwitch()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        GivenMovedWork(_oldStandInId, Guid.NewGuid());
        _coverMaterializer.ReturnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(MovedWorkHandOverResult.Held);

        (await Apply(Row())).ShouldBeTrue();
    }

    [Test]
    public async Task AHopThatCannotBeRecalculated_RefusesTheSwitch()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        GivenMovedWork(_oldStandInId, Guid.NewGuid());
        _coverMaterializer.ReturnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(MovedWorkHandOverResult.RecalculationFailed);

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));
    }

    [Test]
    public async Task TheHopReceiver_IsRefusedWithAReason_NotAsIneligible()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        GivenMovedWork(_oldStandInId, _alternativeId);
        GivenEligible();

        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        refused.Message.ShouldContain("for this swap");
    }

    [Test]
    public async Task TheReceiverOfAReplacedHop_IsRefusedWithAReason_Too()
    {
        GivenSlotChange();
        GivenWritten(_oldStandInId, Change(_oldStandInId, _alternativeId, Guid.NewGuid()));
        GivenEligible();

        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        refused.Message.ShouldContain("for this swap");
    }

    [Test]
    public async Task AFailedRecalculation_RefusesTheSwitch()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        _coverMaterializer.PassOnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(MovedWorkHandOverResult.RecalculationFailed);

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));
    }

    [Test]
    public async Task TheOldStandInGetsTheirMovedHopBack_AndLosesTheProposal()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        var hop = GivenMovedWork(_oldStandInId, Guid.NewGuid());
        var proposal = OldStandInRow(ReplacementRequestOutcome.Proposed);

        await Apply(Row());

        await _coverMaterializer.Received(1).ReturnMovedWorkAsync(_token, hop.Id, _oldStandInId, Arg.Any<CancellationToken>());
        _requestRepository.Received(1).Remove(proposal);
    }

    [Test]
    public async Task AHopStays_WhenTheOldStandInStillStandsInAtThatTime()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        GivenMovedWork(_oldStandInId, Guid.NewGuid());
        _workChangeRepository
            .HasOverlappingScenarioReplacementAsync(_token, _oldStandInId, Day, Start, Start, End, Arg.Any<CancellationToken>())
            .Returns(true);

        await Apply(Row());

        await _coverMaterializer.DidNotReceiveWithAnyArgs().ReturnMovedWorkAsync(default, default, default, default);
    }

    [Test]
    public async Task AnotherMovedWork_KeepsTheTemporaryMembership()
    {
        GivenMovedWork(_absentId, _oldStandInId);
        _workRepository.HoldsMovedWorkInScenarioAsync(_token, _oldStandInId, Arg.Any<CancellationToken>()).Returns(true);

        await Apply(Row());

        await _scenarioService.DidNotReceiveWithAnyArgs().RemoveScenarioMembershipsAsync(default, default, default, default);
    }

    [Test]
    public async Task TheLastCover_RemovesTheTemporaryMembership()
    {
        GivenMovedWork(_absentId, _oldStandInId);

        await Apply(Row());

        await _scenarioService.Received(1).RemoveScenarioMembershipsAsync(_token, _oldStandInId, Day, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AReplacedSlot_GivesAMovedHopBackToo()
    {
        GivenSlotChange();
        var hop = GivenMovedWork(_oldStandInId, Guid.NewGuid());

        await Apply(Row());

        await _coverMaterializer.Received(1).ReturnMovedWorkAsync(_token, hop.Id, _oldStandInId, Arg.Any<CancellationToken>());
    }

    private Work GivenMovedWork(Guid movedFromId, Guid ownerId, Guid? shiftId = null, DateOnly? day = null)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ownerId,
            MovedFromClientId = movedFromId,
            ShiftId = shiftId ?? _cloneShiftId,
            CurrentDate = day ?? Day,
            StartTime = Start,
            EndTime = End,
            AnalyseToken = _token,
        };
        if (!_movedFrom.TryGetValue(movedFromId, out var works))
        {
            works = [];
            _movedFrom[movedFromId] = works;
        }

        works.Add(work);
        _workRepository.ListMovedByScenarioAsync(_token, movedFromId, Day, Arg.Any<CancellationToken>())
            .Returns(_ => works.Where(moved => moved.CurrentDate == Day).ToList());
        _workRepository.ListMovedByScenarioAsync(
                _token, movedFromId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => works
                .Where(moved => moved.CurrentDate >= call.ArgAt<DateOnly>(2) && moved.CurrentDate <= call.ArgAt<DateOnly>(3))
                .ToList());
        return work;
    }

    private WorkChange GivenRelocation(Guid ownerId, Guid receiverId, DateOnly day)
    {
        var relocation = Change(ownerId, receiverId, Guid.NewGuid());
        relocation.Work!.CurrentDate = day;
        GivenWritten(ownerId, relocation);
        return relocation;
    }

    private void GivenOwnAbsence(Guid absentId, DateOnly day, Guid? workId, Guid shiftId)
        => _book.Add(new ReplacementRequest
        {
            Id = Guid.NewGuid(),
            AbsentClientId = absentId,
            CandidateClientId = Guid.NewGuid(),
            ShiftId = shiftId,
            Date = day,
            WorkId = workId,
            Outcome = ReplacementRequestOutcome.Proposed,
            AnalyseToken = _token,
        });

    private Task ShouldReturn(Work work, Guid toClientId)
        => _coverMaterializer.Received(1).ReturnMovedWorkAsync(_token, work.Id, toClientId, Arg.Any<CancellationToken>());

    private Task ShouldNotReturn(Work work)
        => _coverMaterializer.DidNotReceive().ReturnMovedWorkAsync(
            Arg.Any<Guid>(), work.Id, Arg.Any<Guid>(), Arg.Any<CancellationToken>());

    [TestCase(-Reach)]
    [TestCase(3)]
    [TestCase(Reach)]
    public async Task AMovedWorkHandedOnOnAnotherDayOfTheReach_ReturnsToTheOldStandIn(int dayOffset)
    {
        // Arrange
        GivenSlotChange();
        var relief = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(dayOffset));

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(relief, _oldStandInId);
    }

    [TestCase(-Reach - 1)]
    [TestCase(Reach + 1)]
    public async Task AMovedWorkOutsideTheReach_IsNotTouched(int dayOffset)
    {
        // Arrange
        GivenSlotChange();
        var farAway = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(dayOffset));

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldNotReturn(farAway);
    }

    [Test]
    public async Task AMovedSlot_GivesAWorkOfAnotherDayBackToo()
    {
        // Arrange
        GivenMovedWork(_absentId, _oldStandInId);
        var relief = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(2));

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(relief, _oldStandInId);
    }

    [Test]
    public async Task ARecoveryReplacementOnAnotherDay_IsUndone_AndAManualOneStays()
    {
        // Arrange
        GivenSlotChange();
        var otherDay = Day.AddDays(3);
        var relocation = Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid());
        relocation.Work!.CurrentDate = otherDay;
        var manual = Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid());
        manual.Work!.CurrentDate = otherDay;
        manual.Description = "planner note";
        GivenWritten(_oldStandInId, relocation, manual);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<DeleteCommand<WorkChangeResource>>(c => c.Id == relocation.Id), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(
            Arg.Is<DeleteCommand<WorkChangeResource>>(c => c.Id == manual.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InAChainOfThree_TheWorkTheReceiverHandedOn_ReturnsAfterTheFirstOne()
    {
        // Arrange: the old stand-in handed a work to the receiver, who handed an own work on to the chain end.
        var receiverId = Guid.NewGuid();
        var chainEndId = Guid.NewGuid();
        GivenSlotChange();
        var first = GivenMovedWork(_oldStandInId, receiverId, day: Day.AddDays(2));
        var second = GivenMovedWork(receiverId, chainEndId, day: Day.AddDays(4));

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert: the receiver is only free for their own work once the first one is gone.
        Received.InOrder(() =>
        {
            _coverMaterializer.ReturnMovedWorkAsync(_token, first.Id, _oldStandInId, Arg.Any<CancellationToken>());
            _coverMaterializer.ReturnMovedWorkAsync(_token, second.Id, receiverId, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task InAChainOfThree_AReplacedFirstMove_AndAMovedSecondMove_BothReturn()
    {
        // Arrange
        var receiverId = Guid.NewGuid();
        GivenSlotChange();
        var first = GivenRelocation(_oldStandInId, receiverId, Day.AddDays(1));
        var second = GivenMovedWork(receiverId, Guid.NewGuid(), day: Day.AddDays(-2));

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<DeleteCommand<WorkChangeResource>>(c => c.Id == first.Id), Arg.Any<CancellationToken>());
        await ShouldReturn(second, receiverId);
    }

    [Test]
    public async Task ASwapOnTheDayOfTheSlot_ReturnsWhatItsReceiverHandedOnToo()
    {
        // Arrange
        var receiverId = Guid.NewGuid();
        GivenMovedWork(_absentId, _oldStandInId);
        var first = GivenMovedWork(_oldStandInId, receiverId);
        var second = GivenMovedWork(receiverId, Guid.NewGuid());

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(first, _oldStandInId);
        await ShouldReturn(second, receiverId);
    }

    [Test]
    public async Task TheUndo_StopsAtTheChainLengthOfTheEngine()
    {
        // Arrange: a fourth move the engine never builds; whoever wrote it, the switch leaves it alone.
        var receiverId = Guid.NewGuid();
        var chainEndId = Guid.NewGuid();
        GivenSlotChange();
        GivenMovedWork(_oldStandInId, receiverId, day: Day.AddDays(1));
        var second = GivenMovedWork(receiverId, chainEndId, day: Day.AddDays(2));
        var beyond = GivenMovedWork(chainEndId, Guid.NewGuid(), day: Day.AddDays(3));

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(second, receiverId);
        await ShouldNotReturn(beyond);
    }

    [Test]
    public async Task AWorkThatWentAwayForAnOwnAbsence_Stays_WhileAHandedOnWorkReturns()
    {
        // Arrange: the old stand-in is absent themselves two days later; the request book lists that slot by its work.
        GivenSlotChange();
        var ownAbsenceDay = Day.AddDays(2);
        var coveredForThem = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: ownAbsenceDay);
        var handedOn = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(3));
        GivenOwnAbsence(_oldStandInId, ownAbsenceDay, coveredForThem.Id, Guid.NewGuid());

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldNotReturn(coveredForThem);
        await ShouldReturn(handedOn, _oldStandInId);
    }

    [Test]
    public async Task AWorkOfAnOwnAbsence_IsAlsoKnownByShiftAndDay_WhenTheBookHasNoWork()
    {
        // Arrange: the row carries no work id; the work sits on the scenario clone of the row's root shift.
        GivenSlotChange();
        var ownAbsenceDay = Day.AddDays(2);
        var coveredForThem = GivenMovedWork(_oldStandInId, Guid.NewGuid(), _cloneShiftId, ownAbsenceDay);
        GivenOwnAbsence(_oldStandInId, ownAbsenceDay, null, _rootShiftId);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldNotReturn(coveredForThem);
    }

    [Test]
    public async Task ARecoveryReplacementForAnOwnAbsence_Stays()
    {
        // Arrange
        GivenSlotChange();
        var ownAbsenceDay = Day.AddDays(-1);
        var coverOfThem = GivenRelocation(_oldStandInId, Guid.NewGuid(), ownAbsenceDay);
        GivenOwnAbsence(_oldStandInId, ownAbsenceDay, coverOfThem.Work!.Id, Guid.NewGuid());

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheAbsenceOfSomebodyElse_OrOfAnotherDay_ProtectsNothing()
    {
        // Arrange: neither row names this work, this shift on this day or this person.
        GivenSlotChange();
        var reliefDay = Day.AddDays(2);
        var relief = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: reliefDay);
        GivenOwnAbsence(Guid.NewGuid(), reliefDay, relief.Id, _rootShiftId);
        GivenOwnAbsence(_oldStandInId, reliefDay.AddDays(1), null, _rootShiftId);
        GivenOwnAbsence(_oldStandInId, reliefDay, Guid.NewGuid(), Guid.NewGuid());

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(relief, _oldStandInId);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task AnOldStandInWithAnotherCover_AndAWorkHandedOnOnAnotherDay_RefusesTheSwitch(
        bool standsInElsewhere, bool holdsAMovedWork)
    {
        // Arrange: it is not clear which of the two covers the work was handed on for.
        GivenSlotChange();
        var relief = GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(2));
        var proposal = OldStandInRow(ReplacementRequestOutcome.Proposed);
        _workChangeRepository.IsReplacementInScenarioAsync(_token, _oldStandInId, Arg.Any<CancellationToken>())
            .Returns(standsInElsewhere);
        _workRepository.HoldsMovedWorkInScenarioAsync(_token, _oldStandInId, Arg.Any<CancellationToken>())
            .Returns(holdsAMovedWork);

        // Act
        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        // Assert: nothing of the chain was taken back before the refusal.
        refused.Message.ShouldContain(ChainRefusal);
        await ShouldNotReturn(relief);
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
        _requestRepository.DidNotReceive().Remove(proposal);
        await _scenarioService.DidNotReceiveWithAnyArgs().RemoveScenarioMembershipsAsync(default, default, default, default);
    }

    [Test]
    public async Task AnOldStandInWithAnotherCover_StillGetsASwapOfTheSlotsDayBack()
    {
        // Arrange: on the day of the slot the work returns as soon as its person is free at its time.
        GivenSlotChange();
        var hop = GivenMovedWork(_oldStandInId, Guid.NewGuid());
        _workChangeRepository.IsReplacementInScenarioAsync(_token, _oldStandInId, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(hop, _oldStandInId);
    }

    [Test]
    public async Task AReceiverWithAnotherCover_WhoHandedAWorkOn_RefusesTheSwitch()
    {
        // Arrange
        var receiverId = Guid.NewGuid();
        GivenSlotChange();
        GivenMovedWork(_oldStandInId, receiverId, day: Day.AddDays(2));
        var second = GivenMovedWork(receiverId, Guid.NewGuid(), day: Day.AddDays(4));
        _workChangeRepository.IsReplacementInScenarioAsync(_token, receiverId, Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        // Assert
        refused.Message.ShouldContain(ChainRefusal);
        await ShouldNotReturn(second);
    }

    [Test]
    public async Task AWorkOfAnotherDayThatSomethingHolds_RefusesTheSwitch()
    {
        // Arrange
        GivenSlotChange();
        GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(2));
        _coverMaterializer.ReturnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(MovedWorkHandOverResult.Held);

        // Act
        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        // Assert
        refused.Message.ShouldContain(ChainRefusal);
    }

    [Test]
    public async Task AWorkOfAnotherDayThatCannotBeRecalculated_RefusesTheSwitch()
    {
        // Arrange
        GivenSlotChange();
        GivenMovedWork(_oldStandInId, Guid.NewGuid(), day: Day.AddDays(2));
        _coverMaterializer.ReturnMovedWorkAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(MovedWorkHandOverResult.RecalculationFailed);

        // Act
        var refused = await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        // Assert
        refused.Message.ShouldContain("could not be recalculated");
    }

    [Test]
    public async Task TheReceiverOfAReturnedWork_LosesTheTemporaryMembershipOfThatDay()
    {
        // Arrange
        var borrowedId = Guid.NewGuid();
        var reliefDay = Day.AddDays(2);
        GivenSlotChange();
        GivenMovedWork(_oldStandInId, borrowedId, day: reliefDay);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _scenarioService.Received(1).RemoveScenarioMembershipsAsync(_token, borrowedId, reliefDay, Arg.Any<CancellationToken>());
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task TheReceiverOfAReturnedWork_KeepsTheMembership_WhileTheyHoldSomethingOfTheScenario(
        bool standsInElsewhere, bool holdsAMovedWork)
    {
        // Arrange
        var borrowedId = Guid.NewGuid();
        GivenSlotChange();
        var relief = GivenMovedWork(_oldStandInId, borrowedId, day: Day.AddDays(2));
        _workChangeRepository.IsReplacementInScenarioAsync(_token, borrowedId, Arg.Any<CancellationToken>())
            .Returns(standsInElsewhere);
        _workRepository.HoldsMovedWorkInScenarioAsync(_token, borrowedId, Arg.Any<CancellationToken>())
            .Returns(holdsAMovedWork);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await ShouldReturn(relief, _oldStandInId);
        await _scenarioService.DidNotReceive().RemoveScenarioMembershipsAsync(
            Arg.Any<Guid>(), borrowedId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheChainEndOfThree_LosesTheMembershipOfTheDayOfItsWork()
    {
        // Arrange
        var receiverId = Guid.NewGuid();
        var borrowedId = Guid.NewGuid();
        var lastDay = Day.AddDays(4);
        GivenSlotChange();
        GivenMovedWork(_oldStandInId, receiverId, day: Day.AddDays(2));
        GivenMovedWork(receiverId, borrowedId, day: lastDay);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _scenarioService.Received(1).RemoveScenarioMembershipsAsync(_token, borrowedId, lastDay, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcceptanceOfTheCurrentStandIn_ChangesNothing()
    {
        var slot = GivenSlotChange();
        slot.ReplaceClientId = _alternativeId;

        var result = await Apply(Row());

        result.ShouldBeTrue();
        await AssertNothingWritten();
        await _mediator.DidNotReceive().Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>());
    }

    [TestCase(ReplacementRequestOutcome.Declined)]
    [TestCase(ReplacementRequestOutcome.NotReached)]
    [TestCase(ReplacementRequestOutcome.Requested)]
    public async Task AnyOtherAnswer_LeavesTheScenarioAlone(ReplacementRequestOutcome outcome)
    {
        GivenSlotChange();
        var row = Row();
        row.Outcome = outcome;

        var result = await Apply(row);

        result.ShouldBeFalse();
        await AssertNothingWritten();
    }

    [Test]
    public async Task ARowOfTheRealPlan_LeavesTheScenarioAlone()
    {
        var row = Row();
        row.AnalyseToken = null;

        var result = await Apply(row);

        result.ShouldBeFalse();
        await AssertNothingWritten();
    }

    [TestCase(AnalyseScenarioStatus.Accepted)]
    [TestCase(AnalyseScenarioStatus.Rejected)]
    public async Task ADecidedScenario_IsNotChanged(AnalyseScenarioStatus status)
    {
        GivenSlotChange();
        _scenario.Status = status;

        var result = await Apply(Row());

        result.ShouldBeFalse();
        await AssertNothingWritten();
    }

    [Test]
    public async Task ASlotWhoseReplacementWasRemoved_OnlyKeepsTheAnswer()
    {
        var result = await Apply(Row());

        result.ShouldBeFalse();
        await AssertNothingWritten();
    }

    [Test]
    public async Task MultiSlotAbsence_OnlyTheAnsweredShiftOfTheDayChanges()
    {
        var answered = Change(_absentId, _oldStandInId, _cloneShiftId);
        var otherShift = Change(_absentId, _oldStandInId, Guid.NewGuid());
        GivenChanges(_absentId, answered, otherShift);

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<PutCommand<WorkChangeResource>>(c => c.Resource.Id == answered.Id),
            Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(
            Arg.Is<PutCommand<WorkChangeResource>>(c => c.Resource.Id == otherShift.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TwoReplacementsOnOneSlot_TheOneStartingWithTheSlotChanges()
    {
        var early = Change(_absentId, _oldStandInId, _cloneShiftId);
        var late = Change(_absentId, Guid.NewGuid(), _cloneShiftId);
        late.StartTime = new TimeOnly(11, 0);
        GivenChanges(_absentId, early, late);

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<PutCommand<WorkChangeResource>>(c => c.Resource.Id == early.Id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAmbiguousSlot_IsRefused_NothingWritten()
    {
        GivenChanges(
            _absentId,
            Change(_absentId, _oldStandInId, _cloneShiftId),
            Change(_absentId, Guid.NewGuid(), _cloneShiftId));

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        await AssertNothingWritten();
    }

    [Test]
    public async Task ACandidateWhoIsNoLongerEligible_IsRefused_NothingWritten()
    {
        GivenSlotChange();
        GivenEligible(Guid.NewGuid());

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        await AssertNothingWritten();
    }

    [Test]
    public async Task ACandidateAlreadyStandingInAtThatTime_IsRefused_NothingWritten()
    {
        GivenSlotChange();
        _workChangeRepository
            .HasOverlappingScenarioReplacementAsync(_token, _alternativeId, Day, Start, Start, End, Arg.Any<CancellationToken>())
            .Returns(true);

        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        await AssertNothingWritten();
    }

    [Test]
    public async Task TheOverlapCheck_PlacesTheSlotWithTheStartOfItsWork()
    {
        var nightWorkStart = new TimeOnly(23, 0);
        var slot = Change(_absentId, _oldStandInId, _cloneShiftId);
        slot.Work!.StartTime = nightWorkStart;
        GivenChanges(_absentId, slot);

        await Apply(Row());

        await _workChangeRepository.Received(1).HasOverlappingScenarioReplacementAsync(
            _token, _alternativeId, Day, nightWorkStart, Start, End, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheEligibilitySearch_RunsInTheScenarioOnTheSlot_WithTheOverride()
    {
        GivenSlotChange();

        await _switcher.ApplyAcceptedAsync(Row(), overrideBlock: true, CancellationToken.None);

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.ShiftId == _rootShiftId
                && q.Date == Day
                && q.StartTime == Start
                && q.EndTime == End
                && q.GroupId == _groupId
                && q.AnalyseToken == _token
                && q.OverrideBlock),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheEligibilitySearch_PrefersTheScenariosGroup_OverTheGroupOfTheRequest()
    {
        GivenSlotChange();
        var row = Row();
        row.GroupId = Guid.NewGuid();

        await Apply(row);

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.GroupId == _groupId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWithoutGroupRow_UsesTheScenariosGroup_AndWithoutAnyGroupIsRefused()
    {
        GivenSlotChange();
        var row = Row();
        row.GroupId = null;

        await Apply(row);
        _scenario.GroupId = null;

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.GroupId == _groupId), Arg.Any<CancellationToken>());
        await Should.ThrowAsync<ConflictException>(() => Apply(row));
    }

    [Test]
    public async Task TheOldStandInsUntouchedProposal_IsRemoved()
    {
        GivenSlotChange();
        var proposal = OldStandInRow(ReplacementRequestOutcome.Proposed);

        await Apply(Row());

        _requestRepository.Received(1).Remove(proposal);
    }

    [TestCase(ReplacementRequestOutcome.Declined)]
    [TestCase(ReplacementRequestOutcome.NotReached)]
    [TestCase(ReplacementRequestOutcome.Requested)]
    [TestCase(ReplacementRequestOutcome.Accepted)]
    public async Task TheOldStandInsRecordedAnswer_Stays(ReplacementRequestOutcome outcome)
    {
        GivenSlotChange();
        var answered = OldStandInRow(outcome);

        await Apply(Row());

        _requestRepository.DidNotReceive().Remove(answered);
    }

    [Test]
    public async Task TheSwapRelocationOfTheOldStandIn_IsUndone()
    {
        GivenSlotChange();
        var relocation = Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid());
        GivenWritten(_oldStandInId, relocation);

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<DeleteCommand<WorkChangeResource>>(c => c.Id == relocation.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheSwapRelocation_StaysWhileTheOldStandInCoversThatTimeInAnotherSlot()
    {
        GivenSlotChange();
        var relocation = Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid());
        GivenWritten(_oldStandInId, relocation);
        _workChangeRepository
            .HasOverlappingScenarioReplacementAsync(_token, _oldStandInId, Day, Start, Start, End, Arg.Any<CancellationToken>())
            .Returns(true);

        await Apply(Row());

        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheEligibilitySearch_UsesTheStoredWindow_NotTheTimesOfTheRequest()
    {
        GivenSlotChange();
        var row = Row();
        row.StartTime = new TimeOnly(20, 0);
        row.EndTime = new TimeOnly(21, 0);

        await Apply(row);

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.StartTime == Start && q.EndTime == End),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AManualReplacementOnTheOldStandInsOwnWork_Stays()
    {
        GivenSlotChange();
        var manual = Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid());
        manual.Description = "planner note";
        GivenWritten(_oldStandInId, manual);

        await Apply(Row());

        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AHiddenRelocation_RefusesTheWholeSwitch()
    {
        GivenSlotChange();
        GivenWritten(_oldStandInId, Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid()));
        _mediator.Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns((WorkChangeResource?)null);

        await Should.ThrowAsync<KeyNotFoundException>(() => Apply(Row()));
    }

    [Test]
    public async Task AHiddenSlotChange_RefusesTheWholeSwitch()
    {
        GivenSlotChange();
        _mediator.Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>())
            .Returns((WorkChangeResource?)null);

        await Should.ThrowAsync<KeyNotFoundException>(() => Apply(Row()));
    }

    [Test]
    public async Task TheOldStandInsTemporaryMembership_GoesWithTheirLastCover()
    {
        GivenSlotChange();
        _workChangeRepository.IsReplacementInScenarioAsync(_token, _oldStandInId, Arg.Any<CancellationToken>()).Returns(false);

        await Apply(Row());

        await _scenarioService.Received(1).RemoveScenarioMembershipsAsync(_token, _oldStandInId, Day, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheOldStandInsTemporaryMembership_StaysWhileTheyCoverAnotherSlot()
    {
        GivenSlotChange();
        _workChangeRepository.IsReplacementInScenarioAsync(_token, _oldStandInId, Arg.Any<CancellationToken>()).Returns(true);

        await Apply(Row());

        await _scenarioService.DidNotReceive().RemoveScenarioMembershipsAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    private Task<bool> Apply(ReplacementRequest row)
        => _switcher.ApplyAcceptedAsync(row, overrideBlock: false, CancellationToken.None);

    private async Task AssertNothingWritten()
    {
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
        _requestRepository.DidNotReceive().Remove(Arg.Any<ReplacementRequest>());
    }

    private void GivenEligibleBorrowed(Guid candidateId)
        => _mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(
                [new ReplacementCandidate(candidateId, "Candidate", false, [], 0m, IsBorrowed: true)], []));

    [Test]
    public async Task ABorrowedAlternative_JoinsTheGroupInTheScenarioForTheDay()
    {
        // Arrange
        GivenSlotChange();
        GivenEligibleBorrowed(_alternativeId);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _scenarioService.Received(1).AddScenarioMembershipAsync(
            _token, _alternativeId, _groupId, Day, Day, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ABorrowedAlternativeOfAMovedSlot_JoinsTheGroupToo()
    {
        // Arrange
        GivenMovedWork(_absentId, _oldStandInId);
        GivenEligibleBorrowed(_alternativeId);

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _scenarioService.Received(1).AddScenarioMembershipAsync(
            _token, _alternativeId, _groupId, Day, Day, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnAlternativeOfTheOwnGroup_GetsNoTemporaryMembership()
    {
        // Arrange
        GivenSlotChange();

        // Act
        (await Apply(Row())).ShouldBeTrue();

        // Assert
        await _scenarioService.DidNotReceiveWithAnyArgs().AddScenarioMembershipAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task ARefusedBorrowedAlternative_GetsNoMembership()
    {
        // Arrange: the candidate already stands in at that time, so the switch is refused before anything is written.
        GivenSlotChange();
        GivenEligibleBorrowed(_alternativeId);
        _workChangeRepository
            .HasOverlappingScenarioReplacementAsync(_token, _alternativeId, Day, Start, Start, End, Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        await Should.ThrowAsync<ConflictException>(() => Apply(Row()));

        // Assert
        await _scenarioService.DidNotReceiveWithAnyArgs().AddScenarioMembershipAsync(default, default, default, default, default, default);
    }

    [Test]
    public async Task TheEligibilitySearch_JudgesOnlyTheAcceptedCandidate()
    {
        // Arrange
        GivenSlotChange();

        // Act
        await Apply(Row());

        // Assert
        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.OnlyClientId == _alternativeId), Arg.Any<CancellationToken>());
    }

    private void GivenEligible(params Guid[] candidateIds)
        => _mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(
                candidateIds.Select(id => new ReplacementCandidate(id, "Candidate", false, [], 0m)).ToList(),
                []));

    [Test]
    public async Task StandIn_IsCheckedWithTheLeadTrailAndExtraPaidHoursOfTheSlot()
    {
        var slot = GivenSlotChange();
        GivenPadding(slot,
            PaddingChange(WorkChangeType.Briefing, 1m),
            PaddingChange(WorkChangeType.TravelStart, 0.75m),
            PaddingChange(WorkChangeType.Debriefing, 0.5m));

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.LeadHours == 1.75m && q.TrailHours == 0.5m && q.ExtraPaidHours == 0.75m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StandIn_OfAMiddleSlot_TakesNeitherLeadNorTrail()
    {
        var slot = GivenSlotChange();
        slot.StartTime = new TimeOnly(9, 0);
        slot.EndTime = new TimeOnly(11, 0);
        GivenPadding(slot,
            PaddingChange(WorkChangeType.Briefing, 1m),
            PaddingChange(WorkChangeType.Debriefing, 0.5m));

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.LeadHours == 0m && q.TrailHours == 0m && q.ExtraPaidHours == 0m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StandIn_OfAMiddleSlot_TakesOnlyTheTravelInsideTheSlot()
    {
        var slot = GivenSlotChange();
        slot.StartTime = new TimeOnly(9, 0);
        slot.EndTime = new TimeOnly(11, 0);
        GivenPadding(slot,
            PaddingChange(WorkChangeType.TravelWithin, 0.25m, new TimeOnly(10, 0)),
            PaddingChange(WorkChangeType.TravelWithin, 0.5m, new TimeOnly(12, 0)));

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.ExtraPaidHours == 0.25m),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StandIn_OfASlotAfterMidnight_IsSearchedOnTheDayTheSlotStarts()
    {
        var slot = GivenSlotChange();
        slot.Work!.StartTime = new TimeOnly(22, 0);
        slot.Work.EndTime = new TimeOnly(6, 0);
        slot.StartTime = new TimeOnly(2, 0);
        slot.EndTime = new TimeOnly(4, 0);

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<FindReplacementQuery>(q => q.Date == Day.AddDays(1)
                && q.StartTime == new TimeOnly(2, 0) && q.EndTime == new TimeOnly(4, 0)),
            Arg.Any<CancellationToken>());
    }

    private void GivenPadding(WorkChange slot, params WorkChange[] changes)
        => _workChangeRepository
            .ListNonReplacementChangesAsync(slot.Work!.Id, Arg.Any<CancellationToken>())
            .Returns(changes.ToList());

    private static WorkChange PaddingChange(WorkChangeType type, decimal hours, TimeOnly start = default)
        => new() { Id = Guid.NewGuid(), Type = type, ChangeTime = hours, StartTime = start };

    private WorkChange GivenSlotChange()
    {
        var change = Change(_absentId, _oldStandInId, _cloneShiftId);
        GivenChanges(_absentId, change);
        return change;
    }

    private void GivenWritten(Guid clientId, params WorkChange[] changes)
    {
        _workChangeRepository
            .ListReplacementsWrittenByScenarioAsync(_token, clientId, Day, Arg.Any<CancellationToken>())
            .Returns(changes.Where(change => change.Work!.CurrentDate == Day).ToList());
        _workChangeRepository
            .ListReplacementsWrittenByScenarioAsync(
                _token, clientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(call => changes
                .Where(change => change.Work!.CurrentDate >= call.ArgAt<DateOnly>(2)
                    && change.Work.CurrentDate <= call.ArgAt<DateOnly>(3))
                .ToList());
    }

    private void GivenChanges(Guid clientId, params WorkChange[] changes)
        => _workChangeRepository
            .ListScenarioReplacementsAsync(_token, clientId, Day, Arg.Any<CancellationToken>())
            .Returns(changes.ToList());

    private WorkChange Change(Guid ownerId, Guid replacementId, Guid shiftId)
        => new()
        {
            Id = Guid.NewGuid(),
            Type = WorkChangeType.ReplacementWithin,
            StartTime = Start,
            EndTime = End,
            ReplaceClientId = replacementId,
            Description = RecoveryMarkers.WorkChangeSource,
            Work = new Work
            {
                Id = Guid.NewGuid(),
                ClientId = ownerId,
                ShiftId = shiftId,
                CurrentDate = Day,
                StartTime = Start,
                EndTime = End,
                AnalyseToken = _token,
            },
        };

    private ReplacementRequest OldStandInRow(ReplacementRequestOutcome outcome)
    {
        var row = Row();
        row.CandidateClientId = _oldStandInId;
        row.Outcome = outcome;
        _requestRepository.FindLiveAsync(_token, _oldStandInId, _rootShiftId, Day, Arg.Any<CancellationToken>()).Returns(row);
        return row;
    }

    private ReplacementRequest Row()
        => new()
        {
            Id = Guid.NewGuid(),
            AbsentClientId = _absentId,
            CandidateClientId = _alternativeId,
            ShiftId = _rootShiftId,
            Date = Day,
            StartTime = Start,
            EndTime = End,
            GroupId = _groupId,
            Outcome = ReplacementRequestOutcome.Accepted,
            AnalyseToken = _token,
        };
}
