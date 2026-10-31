// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// An accepted alternative becomes the replacement of exactly its slot in the scenario: the existing replacement
/// WorkChange is rewritten through PutCommand (period hours, locks, notifications), the old stand-in's untouched proposal
/// row, swap relocation and temporary membership go with it, recorded answers stay. Everything else is a no-op.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;

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
    private IReplacementRequestRepository _requestRepository = null!;
    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IMediator _mediator = null!;
    private ScenarioReplacementSwitcher _switcher = null!;
    private AnalyseScenario _scenario = null!;

    [SetUp]
    public void SetUp()
    {
        _workChangeRepository = Substitute.For<IWorkChangeRepository>();
        _requestRepository = Substitute.For<IReplacementRequestRepository>();
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

        _switcher = new ScenarioReplacementSwitcher(
            _workChangeRepository, _requestRepository, _scenarioRepository, _scenarioService, _mediator);
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
        GivenChanges(_oldStandInId, relocation);

        await Apply(Row());

        await _mediator.Received(1).Send(
            Arg.Is<DeleteCommand<WorkChangeResource>>(c => c.Id == relocation.Id), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheSwapRelocation_StaysWhileTheOldStandInCoversThatTimeInAnotherSlot()
    {
        GivenSlotChange();
        var relocation = Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid());
        GivenChanges(_oldStandInId, relocation);
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
        GivenChanges(_oldStandInId, manual);

        await Apply(Row());

        await _mediator.DidNotReceive().Send(Arg.Any<DeleteCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AHiddenRelocation_RefusesTheWholeSwitch()
    {
        GivenSlotChange();
        GivenChanges(_oldStandInId, Change(_oldStandInId, Guid.NewGuid(), Guid.NewGuid()));
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

    private void GivenEligible(params Guid[] candidateIds)
        => _mediator.Send(Arg.Any<FindReplacementQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReplacementSearchResult(
                candidateIds.Select(id => new ReplacementCandidate(id, "Candidate", false, [], 0m)).ToList(),
                []));

    private WorkChange GivenSlotChange()
    {
        var change = Change(_absentId, _oldStandInId, _cloneShiftId);
        GivenChanges(_absentId, change);
        return change;
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
