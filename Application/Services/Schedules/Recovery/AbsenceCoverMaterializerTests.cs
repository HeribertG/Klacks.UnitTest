// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The choice per work between moving it and a replacement, and the order after a move. Everything that is not a whole,
/// single cover stays a replacement.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.ScheduleRecovery.Model;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.Recovery;

[TestFixture]
public class AbsenceCoverMaterializerTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly TimeOnly Start = new(8, 0);
    private static readonly TimeOnly End = new(16, 0);
    private static readonly Guid Token = Guid.NewGuid();
    private static readonly Guid Elsa = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Carla = Guid.NewGuid();
    private static readonly Guid SourceWork = Guid.NewGuid();
    private static readonly Guid ClonedWork = Guid.NewGuid();
    private static readonly Guid OtherSourceWork = Guid.NewGuid();
    private static readonly Guid OtherClonedWork = Guid.NewGuid();
    private static readonly DateOnly PeriodStart = new(2026, 11, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 11, 30);

    private IMediator _mediator = null!;
    private ICoverMoveFactsReader _factsReader = null!;
    private IWorkRepository _workRepository = null!;
    private IWorkHandOverService _handOver = null!;
    private IPeriodHoursService _periodHours = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IOvertimeCascadeService _overtime = null!;
    private IScheduleCompletionService _completion = null!;
    private AbsenceCoverMaterializer _materializer = null!;
    private Dictionary<Guid, CoverMoveFacts> _facts = null!;

    [SetUp]
    public void SetUp()
    {
        _facts = new Dictionary<Guid, CoverMoveFacts> { [ClonedWork] = Facts(ClonedWork, Elsa) };
        _mediator = Substitute.For<IMediator>();
        _factsReader = Substitute.For<ICoverMoveFactsReader>();
        _factsReader.LoadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Token, Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, CoverMoveFacts>)_facts);
        _factsReader.IsFreeAsync(Token, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.GetNoTracking(Arg.Any<Guid>())
            .Returns(ci => new Work { Id = ci.Arg<Guid>(), ClientId = Elsa, CurrentDate = Day, AnalyseToken = Token });
        _workRepository.Get(Arg.Any<Guid>())
            .Returns(ci => new Work { Id = ci.Arg<Guid>(), ClientId = Elsa, CurrentDate = Day, AnalyseToken = Token });
        _handOver = Substitute.For<IWorkHandOverService>();
        _handOver.TransferAsync(Arg.Any<Work>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Work>().ClientId = ci.ArgAt<Guid>(1);
                return true;
            });
        _periodHours = Substitute.For<IPeriodHoursService>();
        _periodHours.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _overtime = Substitute.For<IOvertimeCascadeService>();
        _completion = Substitute.For<IScheduleCompletionService>();

        _materializer = new AbsenceCoverMaterializer(
            _mediator, _factsReader, new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc), _workRepository, _handOver, _periodHours, _unitOfWork, _overtime, _completion,
            NullLogger<AbsenceCoverMaterializer>.Instance);
    }

    [Test]
    public async Task AWholeCoverBeforeTheStart_MovesTheWork_AndWritesNoReplacement()
    {
        var moved = await Materialise(Delta(Elsa, Bob));

        moved.ShouldBe(new[] { ClonedWork });
        await _handOver.Received(1).TransferAsync(
            Arg.Is<Work>(w => w.Id == ClonedWork), Bob, Elsa, Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LeadAndTrail_DoNotMakeACoverPartial()
    {
        var moved = await Materialise(Delta(Elsa, Bob) with { LeadHours = 1m, TrailHours = 0.5m, ExtraPaidHours = 1.5m });

        moved.ShouldBe(new[] { ClonedWork });
    }

    [Test]
    public async Task AfterAMove_ItCommits_ThenRunsTheSuccessors_ThenThePeriodHoursOfBothPeople()
    {
        await Materialise(Delta(Elsa, Bob));

        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _handOver.TransferAsync(Arg.Any<Work>(), Bob, Elsa, Arg.Any<CancellationToken>());
            _unitOfWork.CompleteAsync();
            _overtime.ReprocessSuccessorsAsync(
                Arg.Is<Work>(w => w.ClientId == Bob), Arg.Is<Work?>(w => w != null && w.ClientId == Elsa));
            _completion.SaveAndTrackMoveAsync(Bob, Day, PeriodStart, PeriodEnd, Elsa, Day, Token);
        });
    }

    [Test]
    public async Task ARecalculationThatFails_FallsBackToAReplacement_WithoutTheStagedMove()
    {
        _handOver.TransferAsync(Arg.Any<Work>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        var moved = await Materialise(Delta(Elsa, Bob));

        moved.ShouldBeEmpty();
        await _overtime.DidNotReceive().ReprocessSuccessorsAsync(Arg.Any<Work>(), Arg.Any<Work?>());
        await _completion.DidNotReceiveWithAnyArgs().SaveAndTrackMoveAsync(default, default, default, default, default, default, default);
        Received.InOrder(() =>
        {
            _unitOfWork.CompleteAsync();
            _handOver.TransferAsync(Arg.Any<Work>(), Bob, Elsa, Arg.Any<CancellationToken>());
            _unitOfWork.DiscardTrackedChanges();
            _mediator.Send(
                Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.WorkId == ClonedWork && c.Resource.ReplaceClientId == Bob),
                Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task AFallbackWithExtras_IsReported_OneWithoutIsNot()
    {
        _handOver.TransferAsync(Arg.Any<Work>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);
        var withExtras = Delta(Elsa, Bob) with { MovesAsWhole = true, ExtraPaidHours = 1.5m };

        (await MaterialiseAll(withExtras)).RelocationsKeepingExtras.ShouldHaveSingleItem().ShouldBe(withExtras);
        (await MaterialiseAll(withExtras with { ExtraPaidHours = 0m })).RelocationsKeepingExtras.ShouldBeEmpty();
    }

    [Test]
    public async Task AMovedRelocation_IsNotReported()
    {
        var result = await MaterialiseAll(Delta(Elsa, Bob) with { MovesAsWhole = true, ExtraPaidHours = 1.5m });

        result.RelocationsKeepingExtras.ShouldBeEmpty();
    }

    [Test]
    public async Task AMessengerReportAfterTheStart_StaysAReplacement()
    {
        var late = new CoverReport(ReplacementRequestSource.Messenger, AfterTheStart);

        (await MaterialiseAs(late, Delta(Elsa, Bob))).MovedWorkIds.ShouldBeEmpty();

        await _handOver.DidNotReceiveWithAnyArgs().TransferAsync(default!, default, default, default);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.WorkId == ClonedWork), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheDialog_MovesAfterTheStart_Too()
    {
        var late = new CoverReport(ReplacementRequestSource.PlannerDialog, AfterTheStart);

        (await MaterialiseAs(late, Delta(Elsa, Bob))).MovedWorkIds.ShouldBe(new[] { ClonedWork });
    }

    [Test]
    public async Task ALateReport_OffersTheEngineNoMove_AnEarlyOneDoes()
    {
        var map = new Dictionary<Guid, Guid> { [SourceWork] = ClonedWork };

        (await _materializer.FindSourceWorksAsync(
                map, Token, new CoverReport(ReplacementRequestSource.RecoveryEngine, AfterTheStart), CancellationToken.None))
            .Movable.ShouldBeEmpty();
        (await _materializer.FindSourceWorksAsync(
                map, Token, new CoverReport(ReplacementRequestSource.RecoveryEngine, AfterTheStart.AddDays(-1)), CancellationToken.None))
            .Movable.ShouldBe(new[] { SourceWork });
    }

    [Test]
    public async Task PassingOn_KeepsTheOrigin()
    {
        (await _materializer.PassOnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.Done);

        await _handOver.Received(1).TransferAsync(Arg.Any<Work>(), Bob, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AHeldWork_IsNotPassedOn()
    {
        _facts[ClonedWork] = Facts(ClonedWork, Elsa) with { HasReplacement = true };

        (await _materializer.PassOnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.Held);

        await _handOver.DidNotReceiveWithAnyArgs().TransferAsync(default!, default, default, default);
    }

    [Test]
    public async Task AWorkOutsideTheScenario_IsNotPassedOn()
    {
        _facts.Clear();

        (await _materializer.PassOnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.Held);
    }

    [Test]
    public async Task AReturn_GoesToAFreePerson_AndClearsTheOrigin()
    {
        Work? handed = null;
        await _handOver.TransferAsync(Arg.Do<Work>(work => handed = work), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

        (await _materializer.ReturnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.Done);

        handed!.MovedFromClientId.ShouldBeNull();
        await _factsReader.Received(1).IsFreeAsync(Token, Bob, ClonedWork, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AReturn_LeavesTheWork_WhenThePersonIsNotFree()
    {
        _factsReader.IsFreeAsync(Token, Bob, ClonedWork, Arg.Any<CancellationToken>()).Returns(false);

        (await _materializer.ReturnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.Held);

        await _handOver.DidNotReceiveWithAnyArgs().TransferAsync(default!, default, default, default);
    }

    [Test]
    public async Task AReturn_LeavesAHeldWork()
    {
        _facts[ClonedWork] = Facts(ClonedWork, Elsa) with { LockLevel = WorkLockLevel.Confirmed };

        (await _materializer.ReturnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.Held);
    }

    [Test]
    public async Task AFailedRecalculation_IsReported()
    {
        _handOver.TransferAsync(Arg.Any<Work>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        (await _materializer.PassOnMovedWorkAsync(Token, ClonedWork, Bob, CancellationToken.None))
            .ShouldBe(MovedWorkHandOverResult.RecalculationFailed);
    }

    [Test]
    public async Task OnlyUntouchedClones_CountAsUntouchedSources()
    {
        _facts[OtherClonedWork] = Facts(OtherClonedWork, Bob) with { HasLockedChild = true };

        var works = await _materializer.FindSourceWorksAsync(
            new Dictionary<Guid, Guid> { [SourceWork] = ClonedWork, [OtherSourceWork] = OtherClonedWork }, Token, BeforeTheStart, CancellationToken.None);

        works.Movable.ShouldBe(new[] { SourceWork });
    }

    [Test]
    public async Task AContainerThatMayNotMoveAsAWhole_IsHeld_AnotherContainerIsNot()
    {
        _facts[ClonedWork] = Facts(ClonedWork, Elsa) with { IsContainer = true };
        _facts[OtherClonedWork] = Facts(OtherClonedWork, Bob) with { IsContainer = true, HasLockedChild = true };
        var map = new Dictionary<Guid, Guid> { [SourceWork] = ClonedWork, [OtherSourceWork] = OtherClonedWork };

        var beforeTheStart = await _materializer.FindSourceWorksAsync(map, Token, BeforeTheStart, CancellationToken.None);
        var afterTheStart = await _materializer.FindSourceWorksAsync(
            map, Token, new CoverReport(ReplacementRequestSource.Messenger, AfterTheStart), CancellationToken.None);

        beforeTheStart.HeldContainers.ShouldBe(new[] { OtherSourceWork });
        afterTheStart.HeldContainers.ShouldBe(new[] { SourceWork, OtherSourceWork }, ignoreOrder: true);
    }

    [Test]
    public async Task AMovedClone_CountsAsMoved_AndStaysCoverable()
    {
        _facts[ClonedWork] = Facts(ClonedWork, Elsa) with { IsMoved = true };

        var works = await _materializer.FindSourceWorksAsync(
            new Dictionary<Guid, Guid> { [SourceWork] = ClonedWork }, Token, BeforeTheStart, CancellationToken.None);

        works.Moved.ShouldBe(new[] { SourceWork });
        works.Movable.ShouldBe(new[] { SourceWork });
    }

    [Test]
    public async Task OneFailedRecalculation_LeavesTheOtherWorkMoved()
    {
        _facts[OtherClonedWork] = Facts(OtherClonedWork, Bob);
        _handOver.TransferAsync(Arg.Is<Work>(w => w.Id == OtherClonedWork), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var moved = await Materialise(Delta(Bob, Carla) with { SourceWorkIds = [OtherSourceWork] }, Delta(Elsa, Bob));

        moved.ShouldBe(new[] { ClonedWork });
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.WorkId == OtherClonedWork), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AContainerWhoseRecalculationFails_StaysUncovered_WithoutAReplacement()
    {
        _facts[ClonedWork] = Facts(ClonedWork, Elsa) with { IsContainer = true };
        _handOver.TransferAsync(Arg.Is<Work>(w => w.Id == ClonedWork), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var delta = Delta(Elsa, Bob);

        var result = await MaterialiseAll(delta);

        result.MovedWorkIds.ShouldBeEmpty();
        result.HeldContainers.ShouldBe(new[] { delta });
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task APartOfTheWork_StaysAReplacement_OnTheScenarioClone()
    {
        var end = new TimeOnly(12, 0);

        var moved = await Materialise(Delta(Elsa, Bob, end: end));

        moved.ShouldBeEmpty();
        await _handOver.DidNotReceiveWithAnyArgs().TransferAsync(default!, default, default, default);
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c =>
                c.Resource.WorkId == ClonedWork
                && c.Resource.Type == WorkChangeType.ReplacementWithin
                && c.Resource.ReplaceClientId == Bob
                && c.Resource.StartTime == Start
                && c.Resource.EndTime == end
                && c.Resource.Description == RecoveryMarkers.WorkChangeSource),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TwoReceiversForOneWork_StayReplacements()
    {
        var moved = await Materialise(
            Delta(Elsa, Bob, end: new TimeOnly(12, 0)),
            Delta(Elsa, Carla, start: new TimeOnly(12, 0)));

        moved.ShouldBeEmpty();
        await _mediator.Received(2).Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
        await _factsReader.Received(1).LoadAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Distinct().Single() == ClonedWork), Token, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADeltaBackedByTwoWorks_StaysAReplacementOnTheFirst()
    {
        var delta = Delta(Elsa, Bob) with { SourceWorkIds = [SourceWork, OtherSourceWork] };

        var moved = await Materialise(delta);

        moved.ShouldBeEmpty();
        await _mediator.Received(1).Send(
            Arg.Is<PostCommand<WorkChangeResource>>(c => c.Resource.WorkId == ClonedWork), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWorkWithoutFacts_StaysAReplacement()
    {
        _facts.Clear();

        var moved = await Materialise(Delta(Elsa, Bob));

        moved.ShouldBeEmpty();
        await _mediator.Received(1).Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWorkWithoutAClone_IsSkipped()
    {
        var delta = Delta(Elsa, Bob) with { SourceWorkIds = [Guid.NewGuid()] };

        var moved = await Materialise(delta);

        moved.ShouldBeEmpty();
        await _mediator.DidNotReceive().Send(Arg.Any<PostCommand<WorkChangeResource>>(), Arg.Any<CancellationToken>());
        await _handOver.DidNotReceiveWithAnyArgs().TransferAsync(default!, default, default, default);
    }

    [Test]
    public async Task ASwap_MovesTheRelocationHopToo_InTheOrderOfTheEngine()
    {
        _facts[OtherClonedWork] = Facts(OtherClonedWork, Bob);
        var relocation = Delta(Bob, Carla) with { SourceWorkIds = [OtherSourceWork] };
        var cover = Delta(Elsa, Bob);

        var moved = await Materialise(relocation, cover);

        moved.ShouldBe(new[] { OtherClonedWork, ClonedWork }, ignoreOrder: true);
        Received.InOrder(() =>
        {
            _handOver.TransferAsync(Arg.Is<Work>(w => w.Id == OtherClonedWork), Carla, Bob, Arg.Any<CancellationToken>());
            _handOver.TransferAsync(Arg.Is<Work>(w => w.Id == ClonedWork), Bob, Elsa, Arg.Any<CancellationToken>());
        });
    }

    private async Task<IReadOnlySet<Guid>> Materialise(params CellDelta[] deltas)
        => (await MaterialiseAll(deltas)).MovedWorkIds;

    private Task<CoverMaterialisationResult> MaterialiseAll(params CellDelta[] deltas)
        => MaterialiseAs(null, deltas);

    private Task<CoverMaterialisationResult> MaterialiseAs(CoverReport? report, params CellDelta[] deltas)
        => _materializer.MaterialiseAsync(
            new CoverMaterialisation(
                deltas,
                new Dictionary<Guid, Guid> { [SourceWork] = ClonedWork, [OtherSourceWork] = OtherClonedWork },
                Token,
                report ?? BeforeTheStart),
            CancellationToken.None);

    private static readonly DateTime AfterTheStart = DateTime.SpecifyKind(Day.ToDateTime(Start).AddMinutes(1), DateTimeKind.Utc);
    private static readonly CoverReport BeforeTheStart = new(ReplacementRequestSource.RecoveryEngine, AfterTheStart.AddDays(-1));

    private static CoverMoveFacts Facts(Guid workId, Guid owner)
        => new(workId, owner, null, Day, Start, End, WorkLockLevel.None, false, false);

    private static CellDelta Delta(Guid from, Guid to, TimeOnly? start = null, TimeOnly? end = null)
        => new(
            Guid.NewGuid(), Day, from, to, ShiftCategory.Early,
            Day.ToDateTime(start ?? Start), Day.ToDateTime(end ?? End), 8m, EscalationTier.InGroupFree, [SourceWork]);
}
