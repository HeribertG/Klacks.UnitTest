// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroRevertPlanner. An undo needs exactly one selector and is refused when unknown, already undone or
/// itself an undo; every row of the switch is checked, and one conflict (switched again later, changed elsewhere, macro to
/// restore deleted) refuses the whole switch with the list of conflicts; a valid undo restores each shift's own previous
/// macro (or none), and its preview runs the dry run backwards. An undo that removes the reference is warned about and
/// compared as the stored value (production keeps it); Guid.Empty counts as no macro.
/// </summary>

using Klacks.Api.Application.Services.Macros;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Services.Macros;

namespace Klacks.UnitTest.Application.Services.Macros;

[TestFixture]
public class MacroRevertPlannerTests : MacroPlannerTestBase
{
    private const string NoValueAfterwardsText = "no value from the macro used afterwards";
    private const string ReferenceRemovedText = "removes the macro from";

    private IMacroAssignmentHistoryRepository _history = null!;
    private MacroRevertPlanner _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _history = Substitute.For<IMacroAssignmentHistoryRepository>();
        _sut = new MacroRevertPlanner(_references, _history, new MacroOutputChannelInspector(), _dryRun);
    }

    [TestCase(false, false, false)]
    [TestCase(true, true, false)]
    [TestCase(false, true, true)]
    public async Task PlanRevert_NotExactlyOneSelector_IsRefused(bool bySwitch, bool byShift, bool byAbsenceType)
    {
        var request = new MacroRevertRequest(
            bySwitch ? Guid.NewGuid() : null,
            byShift ? Guid.NewGuid() : null,
            byAbsenceType ? Guid.NewGuid() : null);

        var plan = await _sut.PlanRevertAsync(request);

        plan.Refusal!.ShouldContain("exactly one");
    }

    [Test]
    public async Task PlanRevert_UnknownSwitchId_IsRefused()
    {
        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(Guid.NewGuid(), null, null));

        plan.Refusal!.ShouldContain("No macro switch with id");
    }

    [Test]
    public async Task PlanRevert_NoSwitchRecordedForTheShift_IsRefused()
    {
        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(null, Guid.NewGuid(), null));

        plan.Refusal!.ShouldContain("No macro switch made by the assistant");
    }

    [Test]
    public async Task PlanRevert_AlreadyUndone_IsRefused()
    {
        var holder = GivenHolder();
        var entry = GivenEntry(holder, null, Guid.NewGuid());
        entry.RevertedByHistoryId = Guid.NewGuid();

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(entry.SwitchId, null, null));

        plan.Refusal!.ShouldContain("already undone");
    }

    [Test]
    public async Task PlanRevert_AnUndo_IsRefused()
    {
        var holder = GivenHolder();
        var entry = GivenEntry(holder, Guid.NewGuid(), null);
        entry.RevertOfHistoryId = Guid.NewGuid();

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        plan.Refusal!.ShouldContain("itself an undo");
    }

    [Test]
    public async Task PlanRevert_ShiftSwitchedAgainLater_IsRefused()
    {
        var holder = GivenHolder();
        var older = GivenEntry(holder, null, Guid.NewGuid(), isLatest: false);
        GivenEntry(holder, older.NewMacroId, Guid.NewGuid());

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(older.SwitchId, null, null));

        plan.Refusal!.ShouldContain("switched again later");
    }

    [Test]
    public async Task PlanRevert_ChangedElsewhere_IsRefused()
    {
        var switchedTo = GivenMacro();
        var holder = GivenHolder(macroId: Guid.NewGuid());
        GivenEntry(holder, null, switchedTo.Id);

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        plan.Refusal!.ShouldContain("changed outside the assistant");
    }

    [Test]
    public async Task PlanRevert_RestoredMacroDeleted_IsRefused()
    {
        var switchedTo = GivenMacro();
        var holder = GivenHolder(macroId: switchedTo.Id);
        GivenEntry(holder, Guid.NewGuid(), switchedTo.Id);

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        plan.Refusal!.ShouldContain("has been deleted");
    }

    [Test]
    public async Task PlanRevert_OneConflictInTheGroup_RefusesTheWholeSwitch_AndListsTheConflict()
    {
        var previous = GivenMacro();
        var switchedTo = GivenMacro();
        var orderId = Guid.NewGuid();
        var intact = GivenHolder(macroId: switchedTo.Id, cutGroupId: orderId, name: "Cut 1");
        var changed = GivenHolder(macroId: Guid.NewGuid(), cutGroupId: orderId, name: "Cut 2");
        var rows = GivenSwitch(true, (intact, previous.Id, switchedTo.Id), (changed, previous.Id, switchedTo.Id));

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(rows[0].SwitchId, null, null));

        plan.Refusal!.ShouldContain("cannot be undone as a whole");
        plan.Refusal!.ShouldContain("1 of its 2 change(s)");
        plan.Refusal!.ShouldContain("'Cut 2'");
        plan.Refusal!.ShouldNotContain("'Cut 1'");
        plan.Changes.ShouldBeEmpty();
    }

    [Test]
    public async Task PlanRevert_ByShift_RestoresEachShiftsOwnPreviousMacro()
    {
        var firstPrevious = GivenMacro();
        var secondPrevious = GivenMacro();
        var switchedTo = GivenMacro();
        var orderId = Guid.NewGuid();
        var first = GivenHolder(macroId: switchedTo.Id, cutGroupId: orderId, name: "Cut 1");
        var second = GivenHolder(macroId: switchedTo.Id, cutGroupId: orderId, name: "Cut 2");
        var rows = GivenSwitch(true, (first, firstPrevious.Id, switchedTo.Id), (second, secondPrevious.Id, switchedTo.Id));

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(null, second.Id, null));

        plan.Refusal.ShouldBeNull();
        plan.SwitchId.ShouldBe(rows[0].SwitchId);
        plan.Holder.ShouldBe(second);
        plan.Changes.Select(change => change.To).ShouldBe(new[] { firstPrevious, secondPrevious });
        plan.Changes.ShouldAllBe(change => change.From == switchedTo);
        plan.Warnings.ShouldContain(warning => warning.Contains("gets its own previous macro back"));
    }

    [Test]
    public async Task PlanRevert_ToNoMacro_IsAllowed()
    {
        var switchedTo = GivenMacro();
        var holder = GivenHolder(macroId: switchedTo.Id);
        GivenEntry(holder, null, switchedTo.Id);

        var plan = await _sut.PlanRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        plan.Refusal.ShouldBeNull();
        plan.Changes.Single().To.ShouldBeNull();
    }

    [Test]
    public async Task PreviewRevert_RunsTheDryRunBackwards()
    {
        var previous = GivenMacro();
        var switchedTo = GivenMacro();
        var holder = GivenHolder(macroId: switchedTo.Id);
        GivenEntry(holder, previous.Id, switchedTo.Id);

        var preview = await _sut.PreviewRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        preview.Refusal.ShouldBeNull();
        await _dryRun.Received(1).RunAsync(
            MacroAssignmentTarget.Shift,
            Arg.Is<IReadOnlyList<MacroDryRunHolder>>(holders =>
                holders.SequenceEqual(new[] { new MacroDryRunHolder(holder.Id, switchedTo.Id, previous.Id) })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PreviewRevert_ToNoMacro_WarnsThatTheReferenceIsRemoved_AndComparesWithTheStoredValue()
    {
        var switchedTo = GivenMacro();
        var holder = GivenHolder(macroId: switchedTo.Id);
        GivenEntry(holder, null, switchedTo.Id);
        GivenDryRunSamples(Sample(ComputedValue, null));

        var preview = await _sut.PreviewRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        preview.Refusal.ShouldBeNull();
        preview.Plan.Warnings.ShouldContain(warning => warning.Contains(ReferenceRemovedText));
        preview.Plan.Warnings.ShouldNotContain(warning => warning.Contains(NoValueAfterwardsText));
        preview.DryRun!.ChangedSamples.ShouldBe(1);
    }

    [Test]
    public async Task PreviewRevert_PreviousMacroEmpty_RestoresNoMacro_WithoutAConflict()
    {
        var switchedTo = GivenMacro();
        var holder = GivenHolder(macroId: switchedTo.Id);
        GivenEntry(holder, Guid.Empty, switchedTo.Id);

        var preview = await _sut.PreviewRevertAsync(new MacroRevertRequest(null, holder.Id, null));

        preview.Refusal.ShouldBeNull();
        preview.Plan.Changes.Single().To.ShouldBeNull();
        await _dryRun.Received(1).RunAsync(
            MacroAssignmentTarget.Shift,
            Arg.Is<IReadOnlyList<MacroDryRunHolder>>(holders =>
                holders.SequenceEqual(new[] { new MacroDryRunHolder(holder.Id, switchedTo.Id, null) })),
            Arg.Any<CancellationToken>());
    }

    private MacroAssignmentHistory GivenEntry(
        MacroReferenceHolder holder, Guid? previousMacroId, Guid? newMacroId, bool isLatest = true) =>
        GivenSwitch(isLatest, (holder, previousMacroId, newMacroId))[0];

    private IReadOnlyList<MacroAssignmentHistory> GivenSwitch(
        bool isLatest, params (MacroReferenceHolder Holder, Guid? Previous, Guid? Next)[] rows)
    {
        var switchId = Guid.NewGuid();
        IReadOnlyList<MacroAssignmentHistory> entries = rows
            .Select(row => new MacroAssignmentHistory
            {
                Id = Guid.NewGuid(),
                SwitchId = switchId,
                Target = row.Holder.Target,
                TargetId = row.Holder.Id,
                PreviousMacroId = row.Previous,
                NewMacroId = row.Next,
                ChangedByUserId = Guid.NewGuid()
            })
            .ToList();
        _history.GetSwitchAsync(switchId, Arg.Any<CancellationToken>()).Returns(entries);
        if (isLatest)
        {
            foreach (var entry in entries)
            {
                _history.GetLatestAsync(entry.Target, entry.TargetId, Arg.Any<CancellationToken>()).Returns(entry);
            }
        }

        return entries;
    }
}
