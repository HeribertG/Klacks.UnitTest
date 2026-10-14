// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for MacroAssignmentBinding: a switch binds one item per written reference as target|holder|from|to (ids in
/// "N" format, "-" for no macro, Guid.Empty counting as none like in production); the members that are only counted
/// (already on the target) do not flow in; the order of the changes does not matter; an added, removed or re-pointed
/// member changes the fingerprint; an undo binds the macro the holder uses now and the one it gets back (null becomes
/// "-"); more members than the storage cap keep only the fingerprint.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Services.Macros;

namespace Klacks.UnitTest.Domain.Services.Macros;

[TestFixture]
public class MacroAssignmentBindingTests
{
    private const string NoMacro = "-";
    private const string ItemFormat = "{0}|{1:N}|{2}|{3}";

    private static readonly Guid MacroA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MacroB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MacroSnapshot Macro(Guid id) => new(id, "M" + id.ToString("N")[..4], 0, MacroCategoryEnum.Unspecified, MacroOrigin.User, "x");

    private static MacroReferenceHolder Holder(Guid id, Guid? macroId, MacroAssignmentTarget target = MacroAssignmentTarget.Shift) =>
        new(id, target, "H", macroId, null, false, null);

    private static MacroReferenceChange Change(Guid holderId, Guid? from, Guid? to, MacroAssignmentTarget target = MacroAssignmentTarget.Shift) =>
        new(Holder(holderId, from, target), from.HasValue && from != Guid.Empty ? Macro(from.Value) : null, to.HasValue ? Macro(to.Value) : null);

    private static MacroAssignmentPlan AssignPlan(IReadOnlyList<MacroReferenceChange> changes, IReadOnlyList<MacroReferenceHolder>? unchanged = null) =>
        new(changes[0].Holder, Macro(MacroB), changes, unchanged ?? [], [], null);

    private static MacroRevertPlan RevertPlan(IReadOnlyList<MacroReferenceChange> changes) =>
        new(Guid.NewGuid(), changes[0].Holder, changes, [], null);

    [Test]
    public void ASwitch_BindsOneItemPerChange_InTheDocumentedFormat()
    {
        var holder = Guid.NewGuid();

        var binding = MacroAssignmentBinding.Of(AssignPlan([Change(holder, MacroA, MacroB)]));

        binding.Kind.ShouldBe(ConfirmationBindingKinds.MacroSwitch);
        binding.Items.ShouldBe([string.Format(ItemFormat, MacroAssignmentTarget.Shift, holder, MacroA.ToString("N"), MacroB.ToString("N"))]);
    }

    [Test]
    public void AnAbsenceType_IsBoundWithItsTarget()
    {
        var holder = Guid.NewGuid();

        var binding = MacroAssignmentBinding.Of(AssignPlan([Change(holder, null, MacroB, MacroAssignmentTarget.AbsenceType)]));

        binding.Items.ShouldBe([string.Format(ItemFormat, MacroAssignmentTarget.AbsenceType, holder, NoMacro, MacroB.ToString("N"))]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NoPreviousMacro_IsDashed_WhetherNullOrGuidEmpty(bool useGuidEmpty)
    {
        var holder = Guid.NewGuid();
        var change = Change(holder, useGuidEmpty ? Guid.Empty : null, MacroB);

        var binding = MacroAssignmentBinding.Of(AssignPlan([change]));

        binding.Items.Single().ShouldEndWith($"|{NoMacro}|{MacroB:N}");
    }

    [Test]
    public void MembersAlreadyOnTheTarget_AreNotBound()
    {
        var written = Change(Guid.NewGuid(), MacroA, MacroB);
        var alreadyThere = Holder(Guid.NewGuid(), MacroB);

        var withUnchanged = MacroAssignmentBinding.Of(AssignPlan([written], [alreadyThere]));
        var without = MacroAssignmentBinding.Of(AssignPlan([written]));

        withUnchanged.Matches(without).ShouldBeTrue();
        withUnchanged.Items.Count.ShouldBe(1);
    }

    [Test]
    public void TheOrderOfTheChanges_DoesNotMatter()
    {
        var first = Change(Guid.NewGuid(), MacroA, MacroB);
        var second = Change(Guid.NewGuid(), MacroA, MacroB);

        MacroAssignmentBinding.Of(AssignPlan([first, second])).Matches(MacroAssignmentBinding.Of(AssignPlan([second, first]))).ShouldBeTrue();
    }

    [Test]
    public void AMemberAdded_ARemoved_OrRepointed_ChangesTheFingerprint()
    {
        var first = Change(Guid.NewGuid(), MacroA, MacroB);
        var second = Change(Guid.NewGuid(), MacroA, MacroB);
        var shown = MacroAssignmentBinding.Of(AssignPlan([first, second]));

        shown.Matches(MacroAssignmentBinding.Of(AssignPlan([first]))).ShouldBeFalse();
        shown.Matches(MacroAssignmentBinding.Of(AssignPlan([first, second, Change(Guid.NewGuid(), MacroA, MacroB)]))).ShouldBeFalse();
        shown.Matches(MacroAssignmentBinding.Of(AssignPlan([first, Change(second.Holder.Id, MacroB, MacroB)]))).ShouldBeFalse();
        shown.Matches(MacroAssignmentBinding.Of(AssignPlan([first, Change(second.Holder.Id, MacroA, MacroA)]))).ShouldBeFalse();
    }

    [Test]
    public void AnUndo_BindsTheMacroItHasNowAndTheOneItGetsBack_WithNullAsDash()
    {
        var holder = Guid.NewGuid();
        var withPrevious = Change(holder, MacroB, MacroA);
        var withoutPrevious = Change(Guid.NewGuid(), MacroB, null);

        var binding = MacroAssignmentBinding.Of(RevertPlan([withPrevious, withoutPrevious]));

        binding.Kind.ShouldBe(ConfirmationBindingKinds.MacroUndo);
        binding.Items.ShouldContain(string.Format(ItemFormat, MacroAssignmentTarget.Shift, holder, MacroB.ToString("N"), MacroA.ToString("N")));
        binding.Items.ShouldContain(string.Format(ItemFormat, MacroAssignmentTarget.Shift, withoutPrevious.Holder.Id, MacroB.ToString("N"), NoMacro));
    }

    [Test]
    public void ASwitchAndAnUndoOfTheSameChanges_AreNeverTheSameBinding()
    {
        var changes = new[] { Change(Guid.NewGuid(), MacroA, MacroB) };

        MacroAssignmentBinding.Of(AssignPlan(changes)).Matches(MacroAssignmentBinding.Of(RevertPlan(changes))).ShouldBeFalse();
    }

    [Test]
    public void MoreMembersThanTheStorageCap_KeepOnlyTheFingerprint()
    {
        var changes = Enumerable.Range(0, ConfirmationBindingDefaults.MaxStoredItems + 1)
            .Select(_ => Change(Guid.NewGuid(), MacroA, MacroB))
            .ToList();

        var binding = MacroAssignmentBinding.Of(AssignPlan(changes));

        binding.Items.ShouldBeEmpty();
        binding.Fingerprint.ShouldNotBeNullOrEmpty();
        binding.Matches(MacroAssignmentBinding.Of(AssignPlan(changes.AsEnumerable().Reverse().ToList()))).ShouldBeTrue();
    }
}
