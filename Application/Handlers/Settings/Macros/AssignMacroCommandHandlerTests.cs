// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AssignMacroCommandHandler against an in-memory database with the real repositories and planner: a switch
/// writes the holder's macro reference and one history row (macro before and after, requesting user, switch id) and
/// leaves works, breaks and macros untouched — their update time stays empty whether they are sealed or open, and no macro
/// is re-categorised; at the commit the only pending changes are the MacroId column of the switched holders and the new
/// history rows. A shift switch covers every live cut of the same order with one history row per written cut and one
/// shared switch id, and leaves the sealed order row, scenario rows, other orders and cuts already on the macro alone.
/// Every write, the commit and the read-back run inside exactly one transaction; a holder that vanished or changed between
/// the plan and the write aborts the whole group before anything is committed. The handler plans through the preview, so
/// the dry-run warnings reach the outcome, the outcome carries the one dry run of that preview (the skill reports its
/// counts without a second run) and a target macro that cannot run is refused. A switch the plan refuses throws
/// and writes nothing; a read-back mismatch is reported as rolled back. The handler depends on nothing that recalculates;
/// its only addition is a logger: a failure while saving is logged as an error with the switch, holder and macro ids and
/// the holder count (no names, not the database message) and rethrown, while a refused switch is not logged.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.Handlers.Settings.Macro;
using Klacks.Api.Application.Services.Macros;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Macros;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Klacks.UnitTest.TestHelpers;
using MacroEntity = Klacks.Api.Domain.Models.Settings.Macro;

namespace Klacks.UnitTest.Application.Handlers.Settings.Macros;

[TestFixture]
public class AssignMacroCommandHandlerTests
{
    private const decimal SealedStoredValue = 5m;
    private const decimal OpenStoredValue = 7m;
    private const decimal ComputedValue = 4m;
    private const string MacroIdProperty = nameof(Shift.MacroId);
    private const string CommitFailureText = "could not serialize access due to concurrent update (Night)";

    private static readonly ILogger<AssignMacroCommandHandler> AssignLog = NullLogger<AssignMacroCommandHandler>.Instance;

    private DataBaseContext _context = null!;
    private MacroReferenceRepository _references = null!;
    private MacroAssignmentHistoryRepository _history = null!;
    private IMacroDryRunService _dryRun = null!;
    private MacroAssignPlanner _planner = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AssignMacroCommandHandler _sut = null!;
    private bool _inTransaction;
    private bool _completedOutsideTransaction;
    private List<(object Entity, EntityState State, string[] Modified)> _pendingAtCommit = [];

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _references = new MacroReferenceRepository(_context);
        _history = new MacroAssignmentHistoryRepository(_context);
        _dryRun = Substitute.For<IMacroDryRunService>();
        GivenDryRun(new MacroDryRunResult(0, 0, [], null, false));
        _planner = new MacroAssignPlanner(_references, new MacroOutputChannelInspector(), _dryRun);
        _inTransaction = false;
        _completedOutsideTransaction = false;
        _pendingAtCommit = [];
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(async ci =>
            {
                _inTransaction = true;
                try
                {
                    var result = await ci.ArgAt<Func<Task<bool>>>(0)();
                    await _context.SaveChangesAsync();
                    return result;
                }
                finally
                {
                    _inTransaction = false;
                }
            });
        _unitOfWork.CompleteAsync().Returns(async _ =>
        {
            _completedOutsideTransaction |= !_inTransaction;
            _pendingAtCommit = _context.ChangeTracker.Entries()
                .Where(entry => entry.State != EntityState.Unchanged)
                .Select(entry => (entry.Entity, entry.State, entry.Properties
                    .Where(property => property.IsModified)
                    .Select(property => property.Metadata.Name)
                    .ToArray()))
                .ToList();
            await _context.SaveChangesAsync();
        });
        _sut = new AssignMacroCommandHandler(_planner, _references, _history, _unitOfWork, AssignLog);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Shift_WritesOnlyTheReference_RecordsHistory_AndLeavesWorksAndMacrosUntouched()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var shift = AddShift(standard.Id, ShiftStatus.OriginalShift);
        var sealedWork = AddWork(shift.Id, WorkLockLevel.Approved, SealedStoredValue);
        var openWork = AddWork(shift.Id, WorkLockLevel.None, OpenStoredValue);
        await SaveAndClearAsync();
        var userId = Guid.NewGuid();

        var outcome = await _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, shift.Id, copy.Id, userId), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var storedShift = await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == shift.Id);
        storedShift.MacroId.ShouldBe(copy.Id);
        storedShift.UpdateTime.ShouldNotBeNull();
        var entry = await _context.MacroAssignmentHistory.AsNoTracking().SingleAsync();
        entry.SwitchId.ShouldBe(outcome.SwitchId);
        entry.Target.ShouldBe(MacroAssignmentTarget.Shift);
        entry.TargetId.ShouldBe(shift.Id);
        entry.PreviousMacroId.ShouldBe(standard.Id);
        entry.NewMacroId.ShouldBe(copy.Id);
        entry.ChangedByUserId.ShouldBe(userId);
        entry.RevertOfHistoryId.ShouldBeNull();
        entry.RevertedByHistoryId.ShouldBeNull();
        var works = await _context.Work.AsNoTracking().ToListAsync();
        works.Single(w => w.Id == sealedWork.Id).Surcharges.ShouldBe(SealedStoredValue);
        works.Single(w => w.Id == openWork.Id).Surcharges.ShouldBe(OpenStoredValue);
        works.ShouldAllBe(w => w.UpdateTime == null);
        var macros = await _context.Macro.AsNoTracking().ToListAsync();
        macros.ShouldAllBe(m => m.UpdateTime == null);
        macros.Single(m => m.Id == standard.Id).Category.ShouldBe(MacroCategoryEnum.Shift);
    }

    [Test]
    public async Task Shift_PendingChangesAtTheCommit_AreOnlyTheMacroIdColumnAndTheHistoryRows()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var orderId = Guid.NewGuid();
        AddShift(standard.Id, ShiftStatus.SealedOrder, id: orderId);
        var firstCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId);
        var secondCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId);
        AddWork(firstCut.Id, WorkLockLevel.Confirmed, SealedStoredValue);
        AddWork(secondCut.Id, WorkLockLevel.None, OpenStoredValue);
        await SaveAndClearAsync();

        await _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, firstCut.Id, copy.Id, Guid.NewGuid()),
            CancellationToken.None);

        _pendingAtCommit.Count.ShouldBe(4);
        var shifts = _pendingAtCommit.Where(pending => pending.Entity is Shift).ToList();
        shifts.Select(pending => ((Shift)pending.Entity).Id)
            .ShouldBe(new[] { firstCut.Id, secondCut.Id }, ignoreOrder: true);
        shifts.ShouldAllBe(pending => pending.State == EntityState.Modified);
        shifts.ShouldAllBe(pending => pending.Modified.Length == 1 && pending.Modified[0] == MacroIdProperty);
        var rows = _pendingAtCommit.Where(pending => pending.Entity is MacroAssignmentHistory).ToList();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(pending => pending.State == EntityState.Added);
        _completedOutsideTransaction.ShouldBeFalse();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Shift_SwitchesEveryCutOfTheOrder_OneRowPerWrittenCut_WithOneSwitchId()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var orderId = Guid.NewGuid();
        var order = AddShift(standard.Id, ShiftStatus.SealedOrder, id: orderId);
        var firstCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId);
        var secondCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId);
        var cutOnTarget = AddShift(copy.Id, ShiftStatus.SplitShift, orderId);
        var scenarioCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId, analyseToken: Guid.NewGuid());
        var otherOrder = AddShift(standard.Id, ShiftStatus.SplitShift, Guid.NewGuid());
        await SaveAndClearAsync();

        var outcome = await _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, firstCut.Id, copy.Id, Guid.NewGuid()),
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        var shifts = await _context.Shift.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.MacroId);
        shifts[firstCut.Id].ShouldBe(copy.Id);
        shifts[secondCut.Id].ShouldBe(copy.Id);
        shifts[cutOnTarget.Id].ShouldBe(copy.Id);
        shifts[order.Id].ShouldBe(standard.Id);
        shifts[scenarioCut.Id].ShouldBe(standard.Id);
        shifts[otherOrder.Id].ShouldBe(standard.Id);
        var rows = await _context.MacroAssignmentHistory.AsNoTracking().ToListAsync();
        rows.Select(row => row.TargetId).ShouldBe(new[] { firstCut.Id, secondCut.Id }, ignoreOrder: true);
        rows.ShouldAllBe(row => row.SwitchId == outcome.SwitchId && row.PreviousMacroId == standard.Id);
        outcome.Changes.Select(change => change.Holder.Id).ShouldBe(new[] { firstCut.Id, secondCut.Id });
        outcome.Holder.Id.ShouldBe(firstCut.Id);
    }

    [Test]
    public async Task AbsenceType_WritesOnlyTheReference_AndLeavesBreaksUntouched()
    {
        var vacation = AddMacro("Vacation", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var partTime = AddMacro("Vacation part-time", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var absence = AddAbsence(vacation.Id);
        AddBreak(absence.Id, WorkLockLevel.Closed, SealedStoredValue);
        AddBreak(absence.Id, WorkLockLevel.None, OpenStoredValue);
        await SaveAndClearAsync();

        await _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.AbsenceType, absence.Id, partTime.Id, Guid.NewGuid()),
            CancellationToken.None);
        _context.ChangeTracker.Clear();

        (await _context.Absence.AsNoTracking().SingleAsync(a => a.Id == absence.Id)).MacroId.ShouldBe(partTime.Id);
        var breaks = await _context.Break.AsNoTracking().ToListAsync();
        breaks.ShouldAllBe(b => b.UpdateTime == null);
        breaks.Select(b => b.WorkTime).ShouldBe(new[] { SealedStoredValue, OpenStoredValue }, ignoreOrder: true);
        (await _context.MacroAssignmentHistory.AsNoTracking().SingleAsync()).Target.ShouldBe(MacroAssignmentTarget.AbsenceType);
        _pendingAtCommit.Where(pending => pending.Entity is Absence)
            .ShouldAllBe(pending => pending.Modified.Length == 1 && pending.Modified[0] == MacroIdProperty);
        _pendingAtCommit.ShouldAllBe(pending => pending.Entity is Absence || pending.Entity is MacroAssignmentHistory);
    }

    [Test]
    public async Task RefusedSwitch_Throws_AndWritesNothing()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var order = AddShift(standard.Id, ShiftStatus.SealedOrder);
        await SaveAndClearAsync();

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, order.Id, copy.Id, Guid.NewGuid()), CancellationToken.None));

        exception.Message.ShouldContain("sealed order");
        (await _context.MacroAssignmentHistory.AsNoTracking().AnyAsync()).ShouldBeFalse();
        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == order.Id)).MacroId.ShouldBe(standard.Id);
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
    }

    [Test]
    public async Task TargetMacroThatCannotRun_IsRefused_AndWritesNothing()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var broken = AddMacro("Broken copy", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var shift = AddShift(standard.Id, ShiftStatus.OriginalShift);
        await SaveAndClearAsync();
        GivenDryRun(MacroDryRunResult.NewMacroFailed(0, 0, "syntax error"));

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, shift.Id, broken.Id, Guid.NewGuid()), CancellationToken.None));

        exception.Message.ShouldContain("cannot be used");
        (await _context.MacroAssignmentHistory.AsNoTracking().AnyAsync()).ShouldBeFalse();
        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == shift.Id)).MacroId.ShouldBe(standard.Id);
    }

    [Test]
    public async Task DryRunWarnings_ArePassedOnInTheOutcome()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var shift = AddShift(standard.Id, ShiftStatus.OriginalShift);
        await SaveAndClearAsync();
        var sample = new MacroDryRunSample(Guid.NewGuid(), new DateOnly(2026, 6, 10), OpenStoredValue, null, ComputedValue, false);
        GivenDryRun(new MacroDryRunResult(1, 0, [sample], null, false));

        var outcome = await _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, shift.Id, copy.Id, Guid.NewGuid()), CancellationToken.None);

        outcome.Warnings.ShouldContain(warning => warning.Contains("no value from the macro used today"));
    }

    [Test]
    public async Task Outcome_CarriesTheDryRunOfItsPreview_AndNoUndoneSwitch()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var shift = AddShift(standard.Id, ShiftStatus.OriginalShift);
        AddWork(shift.Id, WorkLockLevel.Approved, SealedStoredValue);
        await SaveAndClearAsync();
        var dryRun = new MacroDryRunResult(3, 1, [], null, false);
        GivenDryRun(dryRun);

        var outcome = await _sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, shift.Id, copy.Id, Guid.NewGuid()), CancellationToken.None);

        outcome.DryRun.ShouldBeSameAs(dryRun);
        outcome.UndoneSwitchId.ShouldBeNull();
        await _dryRun.Received(1).RunAsync(
            Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HolderVanishedBetweenPlanAndWrite_AbortsTheWholeGroup_BeforeTheCommit()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var orderId = Guid.NewGuid();
        var firstCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId);
        await SaveAndClearAsync();
        var target = Snapshot(copy);
        var existing = Holder(firstCut.Id, standard.Id, orderId);
        var vanished = Holder(Guid.NewGuid(), standard.Id, orderId);
        var sut = WithPlannedChanges(existing, target, [Change(existing, target), Change(vanished, target)]);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, firstCut.Id, copy.Id, Guid.NewGuid()),
            CancellationToken.None));

        exception.Message.ShouldContain("no longer exists");
        await _unitOfWork.DidNotReceive().CompleteAsync();
        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == firstCut.Id)).MacroId.ShouldBe(standard.Id);
        (await _context.MacroAssignmentHistory.AsNoTracking().AnyAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task HolderChangedBetweenPlanAndWrite_AbortsTheWholeGroup_BeforeTheCommit()
    {
        var standard = AddMacro("AllShift", MacroFunctionEnum.Standard, MacroCategoryEnum.Shift);
        var copy = AddMacro("AllShift extended", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var third = AddMacro("Night special", MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified);
        var orderId = Guid.NewGuid();
        var firstCut = AddShift(standard.Id, ShiftStatus.SplitShift, orderId);
        var secondCut = AddShift(third.Id, ShiftStatus.SplitShift, orderId);
        await SaveAndClearAsync();
        var target = Snapshot(copy);
        var first = Holder(firstCut.Id, standard.Id, orderId);
        var secondAsPlanned = Holder(secondCut.Id, standard.Id, orderId);
        var sut = WithPlannedChanges(first, target, [Change(first, target), Change(secondAsPlanned, target)]);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, firstCut.Id, copy.Id, Guid.NewGuid()),
            CancellationToken.None));

        exception.Message.ShouldContain("changed while");
        await _unitOfWork.DidNotReceive().CompleteAsync();
        var shifts = await _context.Shift.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.MacroId);
        shifts[firstCut.Id].ShouldBe(standard.Id);
        shifts[secondCut.Id].ShouldBe(third.Id);
        (await _context.MacroAssignmentHistory.AsNoTracking().AnyAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task EveryWrite_AndTheCommit_RunInsideOneTransaction()
    {
        var holder = Holder(Guid.NewGuid(), null, null);
        var macro = NewSnapshot();
        var planner = PlannerReturning(holder, macro, [Change(holder, macro)]);
        var references = Substitute.For<IMacroReferenceRepository>();
        var history = Substitute.For<IMacroAssignmentHistoryRepository>();
        var calls = new List<(string Call, bool InTransaction)>();
        references.FindHolderAsync(MacroAssignmentTarget.Shift, holder.Id, Arg.Any<CancellationToken>())
            .Returns(_ => holder, _ => holder with { MacroId = macro.Id });
        references.SetMacroIdAsync(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add((nameof(IMacroReferenceRepository.SetMacroIdAsync), _inTransaction));
                return true;
            });
        history.When(h => h.Add(Arg.Any<MacroAssignmentHistory>()))
            .Do(_ => calls.Add((nameof(IMacroAssignmentHistoryRepository.Add), _inTransaction)));
        var sut = new AssignMacroCommandHandler(planner, references, history, _unitOfWork, AssignLog);

        await sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Guid.NewGuid()), CancellationToken.None);

        calls.Select(call => call.Call).ShouldBe(new[]
        {
            nameof(IMacroReferenceRepository.SetMacroIdAsync), nameof(IMacroAssignmentHistoryRepository.Add)
        });
        calls.ShouldAllBe(call => call.InTransaction);
        _completedOutsideTransaction.ShouldBeFalse();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReadBackMismatch_IsReportedAsRolledBack()
    {
        var holder = Holder(Guid.NewGuid(), null, null);
        var macro = NewSnapshot();
        var planner = PlannerReturning(holder, macro, [Change(holder, macro)]);
        var references = Substitute.For<IMacroReferenceRepository>();
        references.SetMacroIdAsync(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Arg.Any<CancellationToken>())
            .Returns(true);
        references.FindHolderAsync(MacroAssignmentTarget.Shift, holder.Id, Arg.Any<CancellationToken>()).Returns(holder);
        var sut = new AssignMacroCommandHandler(
            planner, references, Substitute.For<IMacroAssignmentHistoryRepository>(), _unitOfWork, AssignLog);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Guid.NewGuid()), CancellationToken.None));

        exception.Message.ShouldContain("could not be confirmed");
    }

    [Test]
    public async Task CommitFailure_IsLoggedWithIdsAndCountButNoNames_AndRethrown()
    {
        var holder = Holder(Guid.NewGuid(), null, null);
        var macro = NewSnapshot();
        var references = Substitute.For<IMacroReferenceRepository>();
        references.FindHolderAsync(MacroAssignmentTarget.Shift, holder.Id, Arg.Any<CancellationToken>()).Returns(holder);
        references.SetMacroIdAsync(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Arg.Any<CancellationToken>())
            .Returns(true);
        var failure = new ConcurrencyException(CommitFailureText);
        _unitOfWork.CompleteAsync().Returns<Task>(_ => throw failure);
        var logger = new RecordingLogger<AssignMacroCommandHandler>();
        var sut = new AssignMacroCommandHandler(
            PlannerReturning(holder, macro, [Change(holder, macro)]),
            references,
            Substitute.For<IMacroAssignmentHistoryRepository>(),
            _unitOfWork,
            logger);

        var thrown = await Should.ThrowAsync<ConcurrencyException>(() => sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Guid.NewGuid()), CancellationToken.None));

        thrown.ShouldBeSameAs(failure);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(failure);
        entry.Message.ShouldContain(holder.Id.ToString());
        entry.Message.ShouldContain(macro.Id.ToString());
        entry.Message.ShouldNotContain(holder.Name);
        entry.Message.ShouldNotContain(macro.Name);
        entry.Message.ShouldNotContain(CommitFailureText);
    }

    [Test]
    public async Task RefusedSwitch_IsNotLoggedAsAnError()
    {
        var logger = new RecordingLogger<AssignMacroCommandHandler>();
        var sut = new AssignMacroCommandHandler(_planner, _references, _history, _unitOfWork, logger);

        await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None));

        logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public void Handler_DependsOnNothingThatRecalculatesOrDispatches()
    {
        var parameters = typeof(AssignMacroCommandHandler).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType);

        parameters.ShouldBe(new[]
        {
            typeof(IMacroAssignPlanner),
            typeof(IMacroReferenceRepository),
            typeof(IMacroAssignmentHistoryRepository),
            typeof(IUnitOfWork),
            typeof(ILogger<AssignMacroCommandHandler>)
        });
    }

    private void GivenDryRun(MacroDryRunResult result) =>
        _dryRun.RunAsync(
                Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private AssignMacroCommandHandler WithPlannedChanges(
        MacroReferenceHolder holder, MacroSnapshot macro, IReadOnlyList<MacroReferenceChange> changes) =>
        new(PlannerReturning(holder, macro, changes), _references, _history, _unitOfWork, AssignLog);

    private static IMacroAssignPlanner PlannerReturning(
        MacroReferenceHolder holder, MacroSnapshot macro, IReadOnlyList<MacroReferenceChange> changes)
    {
        var planner = Substitute.For<IMacroAssignPlanner>();
        var plan = new MacroAssignmentPlan(holder, macro, changes, [], [], null);
        planner.PreviewAssignAsync(MacroAssignmentTarget.Shift, holder.Id, macro.Id, Arg.Any<CancellationToken>())
            .Returns(new MacroAssignmentPreview(plan, new MacroDryRunResult(0, 0, [], null, false), null));
        return planner;
    }

    private static MacroReferenceHolder Holder(Guid id, Guid? macroId, Guid? orderId) =>
        new(id, MacroAssignmentTarget.Shift, "Night", macroId, ShiftStatus.SplitShift, false, orderId);

    private static MacroReferenceChange Change(MacroReferenceHolder holder, MacroSnapshot target) =>
        new(holder, null, target);

    private static MacroSnapshot NewSnapshot() =>
        new(Guid.NewGuid(), "Sunday plus", (int)MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified,
            MacroOrigin.AssistantExtension, "OUTPUT 1, 0");

    private static MacroSnapshot Snapshot(MacroEntity macro) =>
        new(macro.Id, macro.Name, macro.Type, macro.Category, macro.Origin, macro.Content);

    private MacroEntity AddMacro(string name, MacroFunctionEnum function, MacroCategoryEnum category)
    {
        var macro = new MacroEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Content = "OUTPUT 1, 0",
            Type = (int)function,
            Category = category,
            Description = new MultiLanguage()
        };
        _context.Macro.Add(macro);
        return macro;
    }

    private Shift AddShift(
        Guid macroId, ShiftStatus status, Guid? orderId = null, Guid? id = null, Guid? analyseToken = null)
    {
        var shift = new Shift
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Night",
            MacroId = macroId,
            Status = status,
            OriginalId = orderId,
            AnalyseToken = analyseToken
        };
        _context.Shift.Add(shift);
        return shift;
    }

    private Work AddWork(Guid shiftId, WorkLockLevel lockLevel, decimal surcharges)
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            ClientId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2026, 6, 10),
            StartTime = new TimeOnly(22, 0),
            EndTime = new TimeOnly(6, 0),
            WorkTime = 8m,
            Surcharges = surcharges,
            LockLevel = lockLevel
        };
        _context.Work.Add(work);
        return work;
    }

    private Absence AddAbsence(Guid macroId)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = "Ferien" },
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            MacroId = macroId
        };
        _context.Absence.Add(absence);
        return absence;
    }

    private void AddBreak(Guid absenceId, WorkLockLevel lockLevel, decimal workTime)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            AbsenceId = absenceId,
            CurrentDate = new DateOnly(2026, 6, 10),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(12, 0),
            WorkTime = workTime,
            LockLevel = lockLevel
        });
    }

    private async Task SaveAndClearAsync()
    {
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
