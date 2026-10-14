// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for revert_macro_assignment: a caller without the Admin role is refused without a command, an invalid id is
/// refused without a command, a missing switch id (a shift or absence type id instead) is refused without a command, the
/// switch id reaches the command unchanged together with the caller, a valid undo answers with the undo id, the undone
/// switch id and the entry counts of the dry run the handler's own preview produced, a refusal of the command handler (unknown, itself an undo, already undone, conflicts,
/// a switch recorded in between) is relayed verbatim, and a database failure at the commit answers with the save-failed
/// text. The skill depends on the mediator only: the handler resolves the switch, plans and runs the dry run once per
/// confirmed call, so the confirmed call re-checks the role and re-plans instead of trusting the preview it was confirmed
/// with — part of the mitigation of the residual risk the owner accepted on 2026-09-25 (F1). The skill carries the binding
/// of the confirmed preview (context, kind MacroUndo) into the command unchanged; without it, or with a binding of another
/// kind, it refuses and sends nothing, also on a background path. The order of the refusals is: role, parameters, binding.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class RevertMacroAssignmentSkillTests
{
    private const string PlanRefusal = "This macro switch was already undone.";
    private const string HandlerRefusal = "The macro switch to undo is no longer recorded as it was planned; nothing was changed.";
    private const string RawDatabaseMessage = "could not serialize access due to concurrent update";
    private const string ShiftName = "night";
    private const int TotalEntries = 2;
    private const int SealedEntries = 1;

    private static readonly ConfirmationBinding UndoBinding =
        ConfirmationBinding.Of(ConfirmationBindingKinds.MacroUndo, ["Shift|a|b|-"]);

    private IMediator _mediator = null!;
    private RevertMacroAssignmentSkill _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _sut = new RevertMacroAssignmentSkill(_mediator);
    }

    [Test]
    public async Task CallerWithoutTheAdminRole_IsRefused()
    {
        var result = await _sut.ExecuteAsync(Context(Permissions.CanEditSettings), By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MacroAssignmentAccess.AdminOnlyMessage);
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidId_IsRefused_WithoutACommand()
    {
        var result = await _sut.ExecuteAsync(
            Context(Roles.Admin), new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = ShiftName });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("is not a valid id for switchId");
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(MacroAssignmentParameters.ShiftId)]
    [TestCase(MacroAssignmentParameters.AbsenceTypeId)]
    public async Task HolderIdInsteadOfSwitchId_IsRefused_WithoutACommand(string holderParameter)
    {
        var result = await _sut.ExecuteAsync(Context(Roles.Admin), By(holderParameter, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("switchId is required");
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SwitchId_ReachesTheCommandUnchanged()
    {
        var id = Guid.NewGuid();
        var context = Context(Roles.Admin);
        GivenUndo(Guid.NewGuid(), Guid.NewGuid());

        await _sut.ExecuteAsync(context, By(MacroAssignmentParameters.SwitchId, id));

        await _mediator.Received(1).Send(
            Arg.Is<RevertMacroAssignmentCommand>(command =>
                command.SwitchId == id
                && command.UserId == context.UserId
                && ReferenceEquals(command.ConfirmedBinding, UndoBinding)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutABinding_IsRefused_AndSendsNothing_EvenOnABypassedPath()
    {
        var context = Context(Roles.Admin) with { ConfirmedActionBinding = null, BypassAutonomyGate = true };

        var result = await _sut.ExecuteAsync(context, By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, MacroAssignmentSkillNames.Revert));
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASwitchBinding_DoesNotPinAnUndo()
    {
        var context = Context(Roles.Admin) with
        {
            ConfirmedActionBinding = ConfirmationBinding.Of(ConfirmationBindingKinds.MacroSwitch, ["Shift|a|-|b"])
        };

        var result = await _sut.ExecuteAsync(context, By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("only runs as the confirmed replay");
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidUndo_ReportsTheUndoIdAndTheCounts()
    {
        var undoId = Guid.NewGuid();
        var undoneSwitchId = Guid.NewGuid();
        GivenUndo(undoId, undoneSwitchId);

        var result = await _sut.ExecuteAsync(Context(Roles.Admin), By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain($"undo id {undoId}");
        result.Message.ShouldContain("went back from 'Sunday plus' to no macro");
        result.Message.ShouldContain($"Entries in scope: {TotalEntries} ({SealedEntries} sealed");
        result.Data!.GetType().GetProperty("UndoneSwitchId")!.GetValue(result.Data).ShouldBe(undoneSwitchId);
    }

    [TestCase(PlanRefusal)]
    [TestCase(HandlerRefusal)]
    public async Task RefusalOfTheCommandHandler_IsRelayed(string refusal)
    {
        _mediator.Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new InvalidRequestException(refusal));

        var result = await _sut.ExecuteAsync(Context(Roles.Admin), By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(refusal);
    }

    [Test]
    public async Task DatabaseUpdateFailure_AnswersWithTheSaveFailedText_NotTheRawMessage()
    {
        _mediator.Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new DatabaseUpdateException(RawDatabaseMessage));

        var result = await _sut.ExecuteAsync(Context(Roles.Admin), By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MacroAssignmentTextFormatter.SaveFailedMessage);
    }

    [Test]
    public async Task ConcurrencyFailure_AnswersWithTheSaveFailedText()
    {
        _mediator.Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new ConcurrencyException(RawDatabaseMessage));

        var result = await _sut.ExecuteAsync(Context(Roles.Admin), By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MacroAssignmentTextFormatter.SaveFailedMessage);
    }

    [Test]
    public void Skill_DependsOnTheMediatorOnly_SoTheDryRunRunsOncePerCall()
    {
        var parameters = typeof(RevertMacroAssignmentSkill).GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType);

        parameters.ShouldBe(new[] { typeof(IMediator) });
    }

    private void GivenUndo(Guid undoId, Guid undoneSwitchId)
    {
        var change = Change();
        _mediator.Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>())
            .Returns(new MacroAssignmentOutcome(
                undoId, change.Holder, [change], [], new MacroDryRunResult(TotalEntries, SealedEntries, [], null, false),
                undoneSwitchId));
    }

    private static MacroReferenceChange Change()
    {
        var current = new MacroSnapshot(
            Guid.NewGuid(), "Sunday plus", (int)MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified,
            MacroOrigin.AssistantExtension, "OUTPUT 1, 0");
        var holder = new MacroReferenceHolder(
            Guid.NewGuid(), MacroAssignmentTarget.Shift, "Night", current.Id, ShiftStatus.OriginalShift, false, null);
        return new MacroReferenceChange(holder, current, null);
    }

    private static Dictionary<string, object> By(string selector, Guid id) => new() { [selector] = id.ToString() };

    private static SkillExecutionContext Context(params string[] rights) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "caller",
        UserPermissions = rights,
        ConfirmedActionBinding = UndoBinding
    };

    [Test]
    public async Task TheOrderOfTheRefusals_IsRoleThenParametersThenBinding()
    {
        var invalid = new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = ShiftName };
        var valid = By(MacroAssignmentParameters.SwitchId, Guid.NewGuid());
        var withoutBinding = Context(Roles.Admin) with { ConfirmedActionBinding = null };
        var nonAdminWithoutBinding = Context(Permissions.CanEditSettings) with { ConfirmedActionBinding = null };

        var role = await _sut.ExecuteAsync(nonAdminWithoutBinding, invalid);
        var parameter = await _sut.ExecuteAsync(withoutBinding, invalid);
        var binding = await _sut.ExecuteAsync(withoutBinding, valid);

        role.Message.ShouldBe(MacroAssignmentAccess.AdminOnlyMessage);
        parameter.Message!.ShouldContain("is not a valid id for switchId");
        binding.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, MacroAssignmentSkillNames.Revert));
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AWorkBinding_DoesNotPinAnUndo()
    {
        var context = Context(Roles.Admin) with
        {
            ConfirmedActionBinding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, ["w1"])
        };

        var result = await _sut.ExecuteAsync(context, By(MacroAssignmentParameters.SwitchId, Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, MacroAssignmentSkillNames.Revert));
        await _mediator.DidNotReceive().Send(Arg.Any<RevertMacroAssignmentCommand>(), Arg.Any<CancellationToken>());
    }
}
