// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the two macro assignment skills: a caller without the Admin role is refused before anything else, a
/// missing or invalid id is refused without a command, a valid switch sends AssignMacroCommand with the holder kind, both
/// ids and the caller and answers with the switch id, the number of other cuts switched with the shift, the entry counts
/// of the dry run the handler's own preview produced and the notice that nothing is recalculated. A refusal of the command
/// handler (a refused plan, a target macro that cannot run, a holder changed since the plan) is relayed verbatim; a
/// database failure at the commit answers with the save-failed text and never with the raw database message. The skills
/// depend on the mediator only: the handler plans and runs the dry run exactly once per confirmed call, so the confirmed
/// call re-checks the role and re-plans instead of trusting the preview it was confirmed with — part of the mitigation of
/// the residual risk the owner accepted on 2026-09-25 (F1: the confirmation is worded by the model and a token can be
/// redeemed without an explicit yes). The skill carries the binding of the confirmed preview (context, kind MacroSwitch)
/// into the command unchanged; without it, or with a binding of another kind, it refuses and sends nothing, also on a
/// background path. The order of the refusals is: role, parameters, binding.
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
public class AssignMacroSkillTests
{
    private const string PlanRefusal = "'Night' is a sealed order, which never changes again.";
    private const string HandlerRefusal =
        "The macro switch on 'Night' could not be confirmed in the database after the write; the whole switch was rolled back.";
    private const string RawDatabaseMessage = "duplicate key value violates unique constraint \"pk_shift\"";
    private const string MacroName = "Sunday plus";
    private const int TotalEntries = 5;
    private const int SealedEntries = 2;

    private static readonly ConfirmationBinding SwitchBinding =
        ConfirmationBinding.Of(ConfirmationBindingKinds.MacroSwitch, ["Shift|a|-|b"]);

    private IMediator _mediator = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
    }

    [Test]
    public async Task CallerWithoutTheAdminRole_IsRefused_BeforeAnythingElse()
    {
        var result = await ShiftSkill().ExecuteAsync(
            Context(Permissions.CanEditSettings), Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MacroAssignmentAccess.AdminOnlyMessage);
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidMacroId_IsRefused_WithoutACommand()
    {
        var parameters = new Dictionary<string, object>
        {
            [MacroAssignmentParameters.ShiftId] = Guid.NewGuid().ToString(),
            [MacroAssignmentParameters.MacroId] = MacroName
        };

        var result = await ShiftSkill().ExecuteAsync(Context(Roles.Admin), parameters);

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("is not a valid id for macroId");
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MissingShiftId_IsRefused_WithoutACommand()
    {
        var parameters = new Dictionary<string, object> { [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString() };

        var result = await ShiftSkill().ExecuteAsync(Context(Roles.Admin), parameters);

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("shiftId is required");
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidSwitch_SendsTheCommand_AndReportsTheWholeSwitch()
    {
        var shiftId = Guid.NewGuid();
        var macroId = Guid.NewGuid();
        var switchId = Guid.NewGuid();
        var context = Context(Roles.Admin);
        var holder = Holder(shiftId, "Night");
        var macro = Snapshot(macroId);
        var changes = new[]
        {
            new MacroReferenceChange(holder, null, macro),
            new MacroReferenceChange(Holder(Guid.NewGuid(), "Cut 2"), null, macro)
        };
        _mediator.Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>())
            .Returns(new MacroAssignmentOutcome(switchId, holder, changes, [], DryRun(), null));

        var result = await ShiftSkill().ExecuteAsync(context, Parameters(MacroAssignmentParameters.ShiftId, shiftId, macroId));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain($"switch id {switchId}");
        result.Message.ShouldContain("and 1 other cut(s) of its order");
        result.Message.ShouldContain($"Entries in scope: {TotalEntries} ({SealedEntries} sealed");
        result.Message.ShouldContain("Nothing is recalculated automatically");
        await _mediator.Received(1).Send(
            Arg.Is<AssignMacroCommand>(command =>
                command.Target == MacroAssignmentTarget.Shift
                && command.HolderId == shiftId
                && command.MacroId == macroId
                && command.UserId == context.UserId
                && ReferenceEquals(command.ConfirmedBinding, SwitchBinding)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WithoutABinding_IsRefused_AndSendsNothing_EvenOnABypassedPath()
    {
        var context = Context(Roles.Admin) with { ConfirmedActionBinding = null, BypassAutonomyGate = true };

        var result = await ShiftSkill().ExecuteAsync(
            context, Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, MacroAssignmentSkillNames.AssignToShift));
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ABindingOfAnotherKind_IsRefused_AndSendsNothing()
    {
        var context = Context(Roles.Admin) with
        {
            ConfirmedActionBinding = ConfirmationBinding.Of(ConfirmationBindingKinds.Work, ["x"])
        };

        var result = await ShiftSkill().ExecuteAsync(
            context, Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("only runs as the confirmed replay");
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUndoBinding_DoesNotPinASwitch()
    {
        var context = Context(Roles.Admin) with
        {
            ConfirmedActionBinding = ConfirmationBinding.Of(ConfirmationBindingKinds.MacroUndo, ["Shift|a|b|-"])
        };

        var result = await ShiftSkill().ExecuteAsync(
            context, Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheAbsenceTypeSkill_NamesItselfInTheBindingRefusal()
    {
        var context = Context(Roles.Admin) with { ConfirmedActionBinding = null };

        var result = await new AssignMacroToAbsenceTypeSkill(_mediator).ExecuteAsync(
            context, Parameters(MacroAssignmentParameters.AbsenceTypeId, Guid.NewGuid(), Guid.NewGuid()));

        result.Message.ShouldBe(
            string.Format(ConfirmationBindingMessages.BindingRequired, MacroAssignmentSkillNames.AssignToAbsenceType));
    }

    [Test]
    public async Task AParameterNamedLikeTheBinding_IsNeverRead()
    {
        var context = Context(Roles.Admin) with { ConfirmedActionBinding = null };
        var parameters = Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid());
        parameters["confirmedActionBinding"] = SwitchBinding;

        var result = await ShiftSkill().ExecuteAsync(context, parameters);

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }

    [TestCase(PlanRefusal)]
    [TestCase(HandlerRefusal)]
    public async Task RefusalOfTheCommandHandler_IsRelayed(string refusal)
    {
        _mediator.Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new InvalidRequestException(refusal));

        var result = await ShiftSkill().ExecuteAsync(
            Context(Roles.Admin), Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(refusal);
    }

    [Test]
    public async Task DatabaseUpdateFailure_AnswersWithTheSaveFailedText_NotTheRawMessage()
    {
        _mediator.Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new DatabaseUpdateException(RawDatabaseMessage, isDuplicate: true));

        var result = await ShiftSkill().ExecuteAsync(
            Context(Roles.Admin), Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MacroAssignmentTextFormatter.SaveFailedMessage);
    }

    [Test]
    public async Task ConcurrencyFailure_AnswersWithTheSaveFailedText()
    {
        _mediator.Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new ConcurrencyException(RawDatabaseMessage));

        var result = await new AssignMacroToAbsenceTypeSkill(_mediator).ExecuteAsync(
            Context(Roles.Admin), Parameters(MacroAssignmentParameters.AbsenceTypeId, Guid.NewGuid(), Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(MacroAssignmentTextFormatter.SaveFailedMessage);
    }

    [Test]
    public async Task AbsenceTypeSkill_SendsTheAbsenceTypeId()
    {
        var absenceTypeId = Guid.NewGuid();
        var macroId = Guid.NewGuid();
        _mediator.Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroAssignmentOutcome>(_ => throw new InvalidRequestException(PlanRefusal));

        await new AssignMacroToAbsenceTypeSkill(_mediator).ExecuteAsync(
            Context(Roles.Admin), Parameters(MacroAssignmentParameters.AbsenceTypeId, absenceTypeId, macroId));

        await _mediator.Received(1).Send(
            Arg.Is<AssignMacroCommand>(command =>
                command.Target == MacroAssignmentTarget.AbsenceType
                && command.HolderId == absenceTypeId
                && command.MacroId == macroId),
            Arg.Any<CancellationToken>());
    }

    [TestCase(typeof(AssignMacroToShiftSkill))]
    [TestCase(typeof(AssignMacroToAbsenceTypeSkill))]
    public void Skill_DependsOnTheMediatorOnly_SoTheDryRunRunsOncePerCall(Type skillType)
    {
        var parameters = skillType.GetConstructors().Single().GetParameters()
            .Select(parameter => parameter.ParameterType);

        parameters.ShouldBe(new[] { typeof(IMediator) });
    }

    private AssignMacroToShiftSkill ShiftSkill() => new(_mediator);

    private static MacroDryRunResult DryRun() => new(TotalEntries, SealedEntries, [], null, false);

    private static SkillExecutionContext Context(params string[] rights) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "caller",
        UserPermissions = rights,
        ConfirmedActionBinding = SwitchBinding
    };

    private static Dictionary<string, object> Parameters(string holderParameter, Guid holderId, Guid macroId) => new()
    {
        [holderParameter] = holderId.ToString(),
        [MacroAssignmentParameters.MacroId] = macroId.ToString()
    };

    private static MacroReferenceHolder Holder(Guid id, string name) =>
        new(id, MacroAssignmentTarget.Shift, name, null, ShiftStatus.OriginalShift, false, null);

    private static MacroSnapshot Snapshot(Guid id) =>
        new(id, MacroName, (int)MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified, MacroOrigin.AssistantExtension, "OUTPUT 1, 0");

    [Test]
    public async Task TheOrderOfTheRefusals_IsRoleThenParametersThenBinding()
    {
        var invalid = new Dictionary<string, object>
        {
            [MacroAssignmentParameters.ShiftId] = "not-an-id",
            [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString()
        };
        var valid = Parameters(MacroAssignmentParameters.ShiftId, Guid.NewGuid(), Guid.NewGuid());
        var withoutBinding = Context(Roles.Admin) with { ConfirmedActionBinding = null };
        var nonAdminWithoutBinding = Context(Permissions.CanEditSettings) with { ConfirmedActionBinding = null };

        var role = await ShiftSkill().ExecuteAsync(nonAdminWithoutBinding, invalid);
        var parameter = await ShiftSkill().ExecuteAsync(withoutBinding, invalid);
        var binding = await ShiftSkill().ExecuteAsync(withoutBinding, valid);

        role.Message.ShouldBe(MacroAssignmentAccess.AdminOnlyMessage);
        parameter.Message!.ShouldContain("is not a valid id for shiftId");
        binding.Message.ShouldBe(string.Format(ConfirmationBindingMessages.BindingRequired, MacroAssignmentSkillNames.AssignToShift));
        await _mediator.DidNotReceive().Send(Arg.Any<AssignMacroCommand>(), Arg.Any<CancellationToken>());
    }
}
