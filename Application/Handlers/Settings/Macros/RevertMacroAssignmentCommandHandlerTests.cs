// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RevertMacroAssignmentCommandHandler against an in-memory database, end to end with the switch handler:
/// an undo restores the previous macro of every shift of the switch inside one transaction, records one undo row per shift
/// under one new switch id pointing at the undone row and marks every undone row; an undo after the macro of one cut was
/// changed elsewhere is refused as a whole, lists the conflict and changes nothing; undoing the undo is refused (an undo is
/// final). A plan that went stale before the write — the switch was undone and switched again in the meantime — aborts
/// without writing. A switch away from Guid.Empty (no macro, as in production) is undone to null: the switch row keeps
/// Guid.Empty as the previous macro, the undo row records null, and the undo warns that the reference is removed. The
/// outcome carries the one dry run of the handler's preview and the id of the switch it undid. A failure while saving is
/// logged as an error with the undo and switch ids, the holder id and the count (no names, not the database message) and
/// rethrown; a refused undo is not logged. Besides the logger the handler depends on nothing that recalculates.
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
public class RevertMacroAssignmentCommandHandlerTests
{
    private static readonly ILogger<AssignMacroCommandHandler> AssignLog = NullLogger<AssignMacroCommandHandler>.Instance;
    private static readonly ILogger<RevertMacroAssignmentCommandHandler> RevertLog =
        NullLogger<RevertMacroAssignmentCommandHandler>.Instance;

    private const string CommitFailureText = "duplicate key value violates unique constraint (Night)";

    private DbContextOptions<DataBaseContext> _options = null!;
    private DataBaseContext _context = null!;
    private IMacroDryRunService _dryRun = null!;
    private MacroReferenceRepository _references = null!;
    private MacroAssignmentHistoryRepository _history = null!;
    private MacroAssignmentPlanner _planner = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AssignMacroCommandHandler _assign = null!;
    private RevertMacroAssignmentCommandHandler _sut = null!;
    private bool _inTransaction;
    private bool _completedOutsideTransaction;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(_options, null!);
        _references = new MacroReferenceRepository(_context);
        _history = new MacroAssignmentHistoryRepository(_context);
        _dryRun = Substitute.For<IMacroDryRunService>();
        _dryRun.RunAsync(
                Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>())
            .Returns(new MacroDryRunResult(0, 0, [], null, false));
        _planner = new MacroAssignmentPlanner(_references, _history, new MacroOutputChannelInspector(), _dryRun);
        _inTransaction = false;
        _completedOutsideTransaction = false;
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
            await _context.SaveChangesAsync();
        });
        _assign = new AssignMacroCommandHandler(_planner, _references, _history, _unitOfWork, AssignLog);
        _sut = new RevertMacroAssignmentCommandHandler(_planner, _references, _history, _unitOfWork, RevertLog);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Undo_RestoresEveryShiftOfTheSwitch_AndLinksAllRows()
    {
        var (firstCut, secondCut, standard, copy, _) = await SeedOrderAsync();
        var switched = await AssignAsync(firstCut.Id, copy.Id);
        _unitOfWork.ClearReceivedCalls();
        var userId = Guid.NewGuid();

        var undo = await _sut.Handle(
            new RevertMacroAssignmentCommand(null, secondCut.Id, null, userId), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var shifts = await _context.Shift.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.MacroId);
        shifts[firstCut.Id].ShouldBe(standard.Id);
        shifts[secondCut.Id].ShouldBe(standard.Id);
        var rows = await _context.MacroAssignmentHistory.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(4);
        var undoRows = rows.Where(r => r.SwitchId == undo.SwitchId).ToList();
        undoRows.Count.ShouldBe(2);
        undoRows.ShouldAllBe(r => r.PreviousMacroId == copy.Id && r.NewMacroId == standard.Id && r.ChangedByUserId == userId);
        foreach (var undone in rows.Where(r => r.SwitchId == switched.SwitchId))
        {
            var undoRow = undoRows.Single(r => r.Id == undone.RevertedByHistoryId);
            undoRow.RevertOfHistoryId.ShouldBe(undone.Id);
            undoRow.TargetId.ShouldBe(undone.TargetId);
        }

        undo.Changes.Count.ShouldBe(2);
        undo.Holder.Id.ShouldBe(secondCut.Id);
        _completedOutsideTransaction.ShouldBeFalse();
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task UndoAfterAnOutsideChangeOnOneCut_IsRefusedAsAWhole_AndChangesNothing()
    {
        var (firstCut, secondCut, _, copy, third) = await SeedOrderAsync();
        await AssignAsync(firstCut.Id, copy.Id);
        var tracked = await _context.Shift.SingleAsync(s => s.Id == secondCut.Id);
        tracked.MacroId = third.Id;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(
            new RevertMacroAssignmentCommand(null, firstCut.Id, null, Guid.NewGuid()), CancellationToken.None));

        exception.Message.ShouldContain("cannot be undone as a whole");
        exception.Message.ShouldContain("changed outside the assistant");
        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == firstCut.Id)).MacroId.ShouldBe(copy.Id);
        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == secondCut.Id)).MacroId.ShouldBe(third.Id);
        (await _context.MacroAssignmentHistory.AsNoTracking().CountAsync()).ShouldBe(2);
    }

    [Test]
    public async Task UndoingTheUndo_IsRefused()
    {
        var (firstCut, _, _, copy, _) = await SeedOrderAsync();
        await AssignAsync(firstCut.Id, copy.Id);
        await _sut.Handle(new RevertMacroAssignmentCommand(null, firstCut.Id, null, Guid.NewGuid()), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => _sut.Handle(
            new RevertMacroAssignmentCommand(null, firstCut.Id, null, Guid.NewGuid()), CancellationToken.None));

        exception.Message.ShouldContain("itself an undo");
    }

    [Test]
    public async Task StalePlan_SwitchUndoneAndSwitchedAgainMeanwhile_AbortsWithoutWriting()
    {
        var (firstCut, secondCut, _, copy, _) = await SeedOrderAsync();
        var switched = await AssignAsync(firstCut.Id, copy.Id);
        var stalePreview = await _planner.PreviewRevertAsync(new MacroRevertRequest(switched.SwitchId, null, null));
        _context.ChangeTracker.Clear();
        await _sut.Handle(
            new RevertMacroAssignmentCommand(switched.SwitchId, null, null, Guid.NewGuid()), CancellationToken.None);
        _context.ChangeTracker.Clear();
        await AssignAsync(firstCut.Id, copy.Id);
        var rowsBefore = await _context.MacroAssignmentHistory.AsNoTracking().CountAsync();
        var planner = Substitute.For<IMacroAssignmentPlanner>();
        planner.PreviewRevertAsync(Arg.Any<MacroRevertRequest>(), Arg.Any<CancellationToken>()).Returns(stalePreview);
        var sut = new RevertMacroAssignmentCommandHandler(planner, _references, _history, _unitOfWork, RevertLog);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new RevertMacroAssignmentCommand(switched.SwitchId, null, null, Guid.NewGuid()), CancellationToken.None));
        _context.ChangeTracker.Clear();

        exception.Message.ShouldContain("no longer recorded as it was planned");
        (await _context.MacroAssignmentHistory.AsNoTracking().CountAsync()).ShouldBe(rowsBefore);
        var shifts = await _context.Shift.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.MacroId);
        shifts[firstCut.Id].ShouldBe(copy.Id);
        shifts[secondCut.Id].ShouldBe(copy.Id);
    }

    [Test]
    public async Task StalePlan_ConcurrentUndoAndSwitchInAnotherScope_WhileTheRowsAreTrackedHere_AbortsWithoutWriting()
    {
        var (firstCut, secondCut, _, copy, _) = await SeedOrderAsync();
        var switched = await AssignAsync(firstCut.Id, copy.Id);
        var stalePreview = await _planner.PreviewRevertAsync(new MacroRevertRequest(switched.SwitchId, null, null));
        await using (var otherScope = new DataBaseContext(_options, null!))
        {
            var otherReferences = new MacroReferenceRepository(otherScope);
            var otherHistory = new MacroAssignmentHistoryRepository(otherScope);
            var otherPlanner = new MacroAssignmentPlanner(
                otherReferences, otherHistory, new MacroOutputChannelInspector(), _dryRun);
            var otherUnitOfWork = Substitute.For<IUnitOfWork>();
            otherUnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
                .Returns(async ci =>
                {
                    var result = await ci.ArgAt<Func<Task<bool>>>(0)();
                    await otherScope.SaveChangesAsync();
                    return result;
                });
            otherUnitOfWork.CompleteAsync().Returns(async _ => { await otherScope.SaveChangesAsync(); });
            await new RevertMacroAssignmentCommandHandler(
                    otherPlanner, otherReferences, otherHistory, otherUnitOfWork, RevertLog)
                .Handle(new RevertMacroAssignmentCommand(switched.SwitchId, null, null, Guid.NewGuid()), CancellationToken.None);
            await new AssignMacroCommandHandler(otherPlanner, otherReferences, otherHistory, otherUnitOfWork, AssignLog)
                .Handle(new AssignMacroCommand(MacroAssignmentTarget.Shift, firstCut.Id, copy.Id, Guid.NewGuid()),
                    CancellationToken.None);
        }

        var rowsBefore = await _context.MacroAssignmentHistory.AsNoTracking().CountAsync();
        var planner = Substitute.For<IMacroAssignmentPlanner>();
        planner.PreviewRevertAsync(Arg.Any<MacroRevertRequest>(), Arg.Any<CancellationToken>()).Returns(stalePreview);
        var sut = new RevertMacroAssignmentCommandHandler(planner, _references, _history, _unitOfWork, RevertLog);

        var exception = await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new RevertMacroAssignmentCommand(switched.SwitchId, null, null, Guid.NewGuid()), CancellationToken.None));
        _context.ChangeTracker.Clear();

        exception.Message.ShouldContain("no longer recorded as it was planned");
        (await _context.MacroAssignmentHistory.AsNoTracking().CountAsync()).ShouldBe(rowsBefore);
        var shifts = await _context.Shift.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.MacroId);
        shifts[firstCut.Id].ShouldBe(copy.Id);
        shifts[secondCut.Id].ShouldBe(copy.Id);
    }

    [Test]
    public async Task UndoOfASwitchAwayFromGuidEmpty_RestoresNoMacroAsNull_AndWarns()
    {
        var copy = NewMacro("AllShift extended");
        var cut = NewCut(Guid.NewGuid(), Guid.Empty);
        _context.Macro.Add(copy);
        _context.Shift.Add(cut);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var switched = await AssignAsync(cut.Id, copy.Id);

        var undo = await _sut.Handle(
            new RevertMacroAssignmentCommand(switched.SwitchId, null, null, Guid.NewGuid()), CancellationToken.None);
        _context.ChangeTracker.Clear();

        (await _context.Shift.AsNoTracking().SingleAsync(s => s.Id == cut.Id)).MacroId.ShouldBeNull();
        var rows = await _context.MacroAssignmentHistory.AsNoTracking().ToListAsync();
        rows.Single(r => r.SwitchId == switched.SwitchId).PreviousMacroId.ShouldBe(Guid.Empty);
        var undoRow = rows.Single(r => r.SwitchId == undo.SwitchId);
        undoRow.PreviousMacroId.ShouldBe(copy.Id);
        undoRow.NewMacroId.ShouldBeNull();
        undo.Warnings.ShouldContain(warning => warning.Contains("removes the macro from"));
    }

    [Test]
    public async Task Outcome_CarriesTheDryRunOfItsPreview_AndTheUndoneSwitchId()
    {
        var (firstCut, _, _, copy, _) = await SeedOrderAsync();
        var switched = await AssignAsync(firstCut.Id, copy.Id);
        var dryRun = new MacroDryRunResult(4, 2, [], null, false);
        _dryRun.RunAsync(
                Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>())
            .Returns(dryRun);
        _dryRun.ClearReceivedCalls();

        var undo = await _sut.Handle(
            new RevertMacroAssignmentCommand(null, firstCut.Id, null, Guid.NewGuid()), CancellationToken.None);

        undo.DryRun.ShouldBeSameAs(dryRun);
        undo.UndoneSwitchId.ShouldBe(switched.SwitchId);
        await _dryRun.Received(1).RunAsync(
            Arg.Any<MacroAssignmentTarget>(), Arg.Any<IReadOnlyList<MacroDryRunHolder>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CommitFailure_IsLoggedWithIdsAndCountButNoNames_AndRethrown()
    {
        var (firstCut, _, _, copy, _) = await SeedOrderAsync();
        var switched = await AssignAsync(firstCut.Id, copy.Id);
        var failure = new DatabaseUpdateException(CommitFailureText);
        _unitOfWork.CompleteAsync().Returns<Task>(_ => throw failure);
        var logger = new RecordingLogger<RevertMacroAssignmentCommandHandler>();
        var sut = new RevertMacroAssignmentCommandHandler(_planner, _references, _history, _unitOfWork, logger);

        var thrown = await Should.ThrowAsync<DatabaseUpdateException>(() => sut.Handle(
            new RevertMacroAssignmentCommand(switched.SwitchId, null, null, Guid.NewGuid()), CancellationToken.None));

        thrown.ShouldBeSameAs(failure);
        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(failure);
        entry.Message.ShouldContain(switched.SwitchId.ToString());
        entry.Message.ShouldContain(firstCut.Id.ToString());
        entry.Message.ShouldNotContain("Night");
        entry.Message.ShouldNotContain("AllShift");
        entry.Message.ShouldNotContain(CommitFailureText);
    }

    [Test]
    public async Task RefusedUndo_IsNotLoggedAsAnError()
    {
        var logger = new RecordingLogger<RevertMacroAssignmentCommandHandler>();
        var sut = new RevertMacroAssignmentCommandHandler(_planner, _references, _history, _unitOfWork, logger);

        await Should.ThrowAsync<InvalidRequestException>(() => sut.Handle(
            new RevertMacroAssignmentCommand(Guid.NewGuid(), null, null, Guid.NewGuid()), CancellationToken.None));

        logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public void Handler_DependsOnNothingThatRecalculatesOrDispatches()
    {
        var parameters = typeof(RevertMacroAssignmentCommandHandler).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType);

        parameters.ShouldBe(new[]
        {
            typeof(IMacroAssignmentPlanner),
            typeof(IMacroReferenceRepository),
            typeof(IMacroAssignmentHistoryRepository),
            typeof(IUnitOfWork),
            typeof(ILogger<RevertMacroAssignmentCommandHandler>)
        });
    }

    private async Task<MacroAssignmentOutcome> AssignAsync(Guid shiftId, Guid macroId)
    {
        var outcome = await _assign.Handle(
            new AssignMacroCommand(MacroAssignmentTarget.Shift, shiftId, macroId, Guid.NewGuid()),
            CancellationToken.None);
        _context.ChangeTracker.Clear();
        return outcome;
    }

    private async Task<(Shift FirstCut, Shift SecondCut, MacroEntity Standard, MacroEntity Copy, MacroEntity Third)>
        SeedOrderAsync()
    {
        var standard = NewMacro("AllShift");
        var copy = NewMacro("AllShift extended");
        var third = NewMacro("Night special");
        var orderId = Guid.NewGuid();
        var firstCut = NewCut(orderId, standard.Id);
        var secondCut = NewCut(orderId, standard.Id);
        _context.Macro.AddRange(standard, copy, third);
        _context.Shift.AddRange(firstCut, secondCut);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (firstCut, secondCut, standard, copy, third);
    }

    private static Shift NewCut(Guid orderId, Guid macroId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Night",
        MacroId = macroId,
        Status = ShiftStatus.SplitShift,
        OriginalId = orderId
    };

    private static MacroEntity NewMacro(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Content = "OUTPUT 1, 0",
        Description = new MultiLanguage()
    };
}
