// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroAssignmentConfirmationPreviewProvider: it supports exactly the three macro assignment skills, refuses
/// a caller without the Admin role before planning, refuses a missing id and a refused plan (for an undo as well), shows
/// the formatted preview of a valid switch, plans an absence type switch for the absence type, and shows the undo preview
/// of a valid undo addressed by its switch id.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Macros;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroAssignmentConfirmationPreviewProviderTests
{
    private const string PlanRefusal = "No shift with id 'x' exists.";
    private const string RevertRefusal = "This macro switch was already undone.";
    private const string OtherSkillName = "update_macro";

    private IMacroAssignPlanner _assignPlanner = null!;
    private IMacroRevertPlanner _revertPlanner = null!;
    private MacroAssignmentConfirmationPreviewProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _assignPlanner = Substitute.For<IMacroAssignPlanner>();
        _revertPlanner = Substitute.For<IMacroRevertPlanner>();
        _sut = new MacroAssignmentConfirmationPreviewProvider(_assignPlanner, _revertPlanner);
    }

    [TestCase(MacroAssignmentSkillNames.AssignToShift, true)]
    [TestCase(MacroAssignmentSkillNames.AssignToAbsenceType, true)]
    [TestCase(MacroAssignmentSkillNames.Revert, true)]
    [TestCase(OtherSkillName, false)]
    public void Supports_ExactlyTheMacroAssignmentSkills(string skillName, bool expected)
    {
        _sut.Supports(skillName).ShouldBe(expected);
    }

    [Test]
    public async Task CallerWithoutTheAdminRole_IsRefused_BeforePlanning()
    {
        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.AssignToShift, Context(Permissions.CanEditSettings), AssignParameters(MacroAssignmentParameters.ShiftId));

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe(MacroAssignmentAccess.AdminOnlyMessage);
        await _assignPlanner.DidNotReceiveWithAnyArgs().PreviewAssignAsync(default, default, default, default);
    }

    [Test]
    public async Task CallerWithoutTheAdminRole_IsRefused_BeforePlanningAnUndo()
    {
        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.Revert,
            Context(Permissions.CanEditSettings),
            new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = Guid.NewGuid().ToString() });

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe(MacroAssignmentAccess.AdminOnlyMessage);
        await _revertPlanner.DidNotReceiveWithAnyArgs().PreviewRevertAsync(default!, default);
    }

    [Test]
    public async Task MissingId_IsRefused()
    {
        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.AssignToShift, Context(Roles.Admin), new Dictionary<string, object>());

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldContain("shiftId is required");
    }

    [Test]
    public async Task RefusedPlan_IsRefused()
    {
        _assignPlanner.PreviewAssignAsync(Arg.Any<MacroAssignmentTarget>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MacroAssignmentPreview(MacroAssignmentPlan.Refused(PlanRefusal), null, PlanRefusal));

        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.AssignToShift, Context(Roles.Admin), AssignParameters(MacroAssignmentParameters.ShiftId));

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe(PlanRefusal);
    }

    [Test]
    public async Task RefusedUndo_IsRefused()
    {
        _revertPlanner.PreviewRevertAsync(Arg.Any<MacroRevertRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MacroRevertPreview(MacroRevertPlan.Refused(RevertRefusal), null, RevertRefusal));

        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.Revert,
            Context(Roles.Admin),
            new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = Guid.NewGuid().ToString() });

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe(RevertRefusal);
    }

    [Test]
    public async Task ValidSwitch_ShowsTheFormattedPreview()
    {
        var change = new MacroReferenceChange(Holder(), null, Snapshot("Sunday plus"));
        var plan = new MacroAssignmentPlan(change.Holder, change.To, [change], [], [], null);
        _assignPlanner.PreviewAssignAsync(MacroAssignmentTarget.Shift, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MacroAssignmentPreview(plan, new MacroDryRunResult(3, 1, [], null, false), null));

        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.AssignToShift, Context(Roles.Admin), AssignParameters(MacroAssignmentParameters.ShiftId));

        preview.IsRefusal.ShouldBeFalse();
        preview.Text.ShouldContain("'Night'");
        preview.Text.ShouldContain("'Sunday plus'");
        preview.Text.ShouldContain("Entries in scope: 3");
    }

    [Test]
    public async Task AbsenceTypeSkill_PlansForTheAbsenceType()
    {
        _assignPlanner.PreviewAssignAsync(Arg.Any<MacroAssignmentTarget>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new MacroAssignmentPreview(MacroAssignmentPlan.Refused(PlanRefusal), null, PlanRefusal));

        await _sut.BuildAsync(
            MacroAssignmentSkillNames.AssignToAbsenceType, Context(Roles.Admin), AssignParameters(MacroAssignmentParameters.AbsenceTypeId));

        await _assignPlanner.Received(1).PreviewAssignAsync(
            MacroAssignmentTarget.AbsenceType, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ValidUndo_ShowsTheUndoPreview()
    {
        var switchId = Guid.NewGuid();
        var current = Snapshot("Sunday plus");
        var change = new MacroReferenceChange(Holder(current.Id), current, Snapshot("AllShift"));
        var plan = new MacroRevertPlan(switchId, change.Holder, [change], [], null);
        _revertPlanner.PreviewRevertAsync(Arg.Any<MacroRevertRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MacroRevertPreview(plan, new MacroDryRunResult(3, 1, [], null, false), null));

        var preview = await _sut.BuildAsync(
            MacroAssignmentSkillNames.Revert,
            Context(Roles.Admin),
            new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = switchId.ToString() });

        preview.IsRefusal.ShouldBeFalse();
        preview.Text.ShouldContain($"Undo the macro switch {switchId}");
        await _revertPlanner.Received(1).PreviewRevertAsync(new MacroRevertRequest(switchId), Arg.Any<CancellationToken>());
    }

    private static SkillExecutionContext Context(params string[] rights) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "caller",
        UserPermissions = rights
    };

    private static Dictionary<string, object> AssignParameters(string holderParameter) => new()
    {
        [holderParameter] = Guid.NewGuid().ToString(),
        [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString()
    };

    private static MacroReferenceHolder Holder(Guid? macroId = null) =>
        new(Guid.NewGuid(), MacroAssignmentTarget.Shift, "Night", macroId, ShiftStatus.OriginalShift, false, null);

    private static MacroSnapshot Snapshot(string name) =>
        new(Guid.NewGuid(), name, (int)MacroFunctionEnum.Custom, MacroCategoryEnum.Unspecified, MacroOrigin.User, "OUTPUT 1, 0");
}
