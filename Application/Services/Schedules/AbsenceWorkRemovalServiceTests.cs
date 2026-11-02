// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Which works an entered absence takes away: only an untouched work of the caller's groups that the absence covers, never
/// through an on-call absence, and nothing when no group is selected.
/// </summary>

using Klacks.Api.Application.Commands.Works;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class AbsenceWorkRemovalServiceTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly DateOnly PeriodStart = new(2026, 11, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 11, 30);
    private static readonly Guid Elsa = Guid.NewGuid();
    private static readonly Guid Sick = Guid.NewGuid();
    private static readonly Guid OnCall = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();

    private IScheduleEntriesService _entries = null!;
    private ICoverMoveFactsReader _factsReader = null!;
    private IAbsenceRepository _absences = null!;
    private ISelectedGroupContextResolver _groups = null!;
    private IPeriodHoursService _periodHours = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IMediator _mediator = null!;
    private AbsenceWorkRemovalService _service = null!;
    private ScheduleCell _cell = null!;
    private CoverMoveFacts _facts = null!;

    [SetUp]
    public void SetUp()
    {
        _cell = new ScheduleCell
        {
            EntryType = (int)ScheduleEntryType.Work,
            ClientId = Elsa,
            SourceId = WorkId,
            EntryId = Guid.NewGuid(),
            EntryDate = Day.ToDateTime(TimeOnly.MinValue),
            StartTime = new TimeSpan(8, 0, 0),
            EndTime = new TimeSpan(16, 0, 0),
        };
        _facts = new CoverMoveFacts(WorkId, Elsa, null, Day, new TimeOnly(8, 0), new TimeOnly(16, 0), WorkLockLevel.None, false, false);

        _entries = Substitute.For<IScheduleEntriesService>();
        _entries.GetScheduleEntriesAsync(Arg.Any<ScheduleEntriesCriteria>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<ScheduleCell>)[_cell]);
        _factsReader = Substitute.For<ICoverMoveFactsReader>();
        _factsReader.LoadAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<Guid, CoverMoveFacts>)new Dictionary<Guid, CoverMoveFacts> { [WorkId] = _facts });
        _absences = Substitute.For<IAbsenceRepository>();
        _absences.GetOnCallAbsenceIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<Guid> { OnCall });
        _groups = Substitute.For<ISelectedGroupContextResolver>();
        _groups.ResolveVisibleGroupIdsAsync().Returns(new List<Guid> { GroupId });
        _mediator = Substitute.For<IMediator>();
        _periodHours = Substitute.For<IPeriodHoursService>();
        _periodHours.GetPeriodBoundariesAsync(Day).Returns((PeriodStart, PeriodEnd));
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>()).Returns(ci => ci.Arg<Func<Task<int>>>()());
        _service = new AbsenceWorkRemovalService(_entries, _factsReader, _absences, _groups, _periodHours, _unitOfWork, _mediator);
    }

    private static AbsenceSpan FullDay(Guid absenceId) => new(Elsa, absenceId, Day, new TimeOnly(0, 0), new TimeOnly(23, 59));

    private Task<IReadOnlyList<RemovableWork>> Find(AbsenceSpan absence, Guid? token = null)
        => _service.FindAsync([absence], token, CancellationToken.None);

    [Test]
    public async Task AFullDayAbsence_TakesTheWork()
        => (await Find(FullDay(Sick))).ShouldHaveSingleItem().WorkId.ShouldBe(WorkId);

    [Test]
    public async Task AnOnCallAbsence_TakesNothing()
    {
        (await Find(FullDay(OnCall))).ShouldBeEmpty();
        await _entries.DidNotReceiveWithAnyArgs().GetScheduleEntriesAsync(default!, default);
    }

    [Test]
    public async Task APartialAbsence_LeavesAWorkReachingBeyondIt()
        => (await Find(new AbsenceSpan(Elsa, Sick, Day, new TimeOnly(14, 0), new TimeOnly(18, 0)))).ShouldBeEmpty();

    [Test]
    public async Task AWorkInsideAPartialAbsence_IsTaken()
        => (await Find(new AbsenceSpan(Elsa, Sick, Day, new TimeOnly(7, 0), new TimeOnly(17, 0)))).ShouldHaveSingleItem();

    [Test]
    public async Task AWorkOfAForeignGroup_Stays()
    {
        _cell.IsGroupRestricted = true;

        (await Find(FullDay(Sick))).ShouldBeEmpty();
    }

    [Test]
    public async Task WithoutASelectedGroup_NothingIsTaken()
    {
        _groups.ResolveVisibleGroupIdsAsync().Returns((List<Guid>?)null);

        (await Find(FullDay(Sick))).ShouldBeEmpty();
    }

    [TestCase(WorkLockLevel.Confirmed, false, false)]
    [TestCase(WorkLockLevel.None, true, false)]
    [TestCase(WorkLockLevel.None, false, true)]
    public async Task AWorkSomethingHoldsInPlace_Stays(WorkLockLevel lockLevel, bool hasReplacement, bool hasLockedChild)
    {
        _facts = _facts with { LockLevel = lockLevel, HasReplacement = hasReplacement, HasLockedChild = hasLockedChild };

        (await Find(FullDay(Sick))).ShouldBeEmpty();
    }

    [Test]
    public async Task TheCheckReadsThePlanOfTheAbsence()
    {
        var token = Guid.NewGuid();

        await Find(FullDay(Sick), token);

        await _entries.Received(1).GetScheduleEntriesAsync(
            Arg.Is<ScheduleEntriesCriteria>(c =>
                c.AnalyseToken == token && c.ClientIds!.Single() == Elsa && c.EntryType == ScheduleEntryType.Work),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ADurationWithoutTimes_CountsAsTheWholeDay()
        => (await Find(new AbsenceSpan(Elsa, Sick, Day, new TimeOnly(0, 0), new TimeOnly(0, 0)))).ShouldHaveSingleItem();

    [Test]
    public async Task Write_DeletesInOneTransaction_ThroughTheWorkDelete()
    {
        var written = false;

        await _service.WriteAndRemoveAsync([Removable()], () => { written = true; return Task.CompletedTask; }, CancellationToken.None);

        written.ShouldBeTrue();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>());
        await _mediator.Received(1).Send(
            Arg.Is<DeleteWorkCommand>(c => c.Id == WorkId && c.PeriodStart == PeriodStart && c.PeriodEnd == PeriodEnd),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Write_WithoutWorks_OpensNoTransaction()
    {
        var written = false;

        await _service.WriteAndRemoveAsync([], () => { written = true; return Task.CompletedTask; }, CancellationToken.None);

        written.ShouldBeTrue();
        await _unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync(default(Func<Task<int>>)!);
    }

    [Test]
    public async Task Write_AFailure_LeavesNothingStaged()
    {
        _mediator.Send(Arg.Any<DeleteWorkCommand>(), Arg.Any<CancellationToken>())
            .Returns<WorkResource?>(_ => throw new InvalidOperationException("refused"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.WriteAndRemoveAsync([Removable()], () => Task.CompletedTask, CancellationToken.None));

        _unitOfWork.Received(1).DiscardTrackedChanges();
    }

    private static RemovableWork Removable()
        => new(WorkId, Elsa, Guid.NewGuid(), Day, new TimeOnly(8, 0), new TimeOnly(16, 0));
}
