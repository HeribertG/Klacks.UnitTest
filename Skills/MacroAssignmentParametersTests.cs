// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroAssignmentParameters: a switch reads the holder id (shiftId or absenceTypeId) and the macro id,
/// refuses a missing id with the parameter name and a name passed instead of an id; an undo reads the optional selectors
/// (switchId, shiftId, absenceTypeId) and refuses an invalid id.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Macros;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroAssignmentParametersTests
{
    private const string MacroName = "Sunday plus";
    private const string ShiftName = "night";

    [Test]
    public void ReadAssign_Shift_ReadsBothIds()
    {
        var shiftId = Guid.NewGuid();
        var macroId = Guid.NewGuid();

        var (holderId, readMacroId, error) = MacroAssignmentParameters.ReadAssign(
            new Dictionary<string, object>
            {
                [MacroAssignmentParameters.ShiftId] = shiftId.ToString(),
                [MacroAssignmentParameters.MacroId] = macroId.ToString()
            },
            MacroAssignmentTarget.Shift);

        error.ShouldBeNull();
        holderId.ShouldBe(shiftId);
        readMacroId.ShouldBe(macroId);
    }

    [Test]
    public void ReadAssign_AbsenceType_ReadsTheAbsenceTypeId()
    {
        var absenceTypeId = Guid.NewGuid();

        var (holderId, _, error) = MacroAssignmentParameters.ReadAssign(
            new Dictionary<string, object>
            {
                [MacroAssignmentParameters.AbsenceTypeId] = absenceTypeId.ToString(),
                [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString()
            },
            MacroAssignmentTarget.AbsenceType);

        error.ShouldBeNull();
        holderId.ShouldBe(absenceTypeId);
    }

    [Test]
    public void ReadAssign_MissingHolder_NamesTheParameter()
    {
        var (_, _, error) = MacroAssignmentParameters.ReadAssign(
            new Dictionary<string, object> { [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString() },
            MacroAssignmentTarget.Shift);

        error!.ShouldContain("shiftId is required");
    }

    [Test]
    public void ReadAssign_NameInsteadOfAnId_IsRefused()
    {
        var (_, _, error) = MacroAssignmentParameters.ReadAssign(
            new Dictionary<string, object>
            {
                [MacroAssignmentParameters.ShiftId] = Guid.NewGuid().ToString(),
                [MacroAssignmentParameters.MacroId] = MacroName
            },
            MacroAssignmentTarget.Shift);

        error!.ShouldContain("is not a valid id for macroId");
    }

    [Test]
    public void ReadRevert_ReadsTheSwitchId()
    {
        var switchId = Guid.NewGuid();

        var (request, error) = MacroAssignmentParameters.ReadRevert(
            new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = switchId.ToString() });

        error.ShouldBeNull();
        request.ShouldBe(new MacroRevertRequest(switchId));
    }

    [Test]
    public void ReadRevert_InvalidId_IsRefused()
    {
        var (request, error) = MacroAssignmentParameters.ReadRevert(
            new Dictionary<string, object> { [MacroAssignmentParameters.SwitchId] = ShiftName });

        request.ShouldBeNull();
        error!.ShouldContain("is not a valid id for switchId");
    }

    [TestCase(MacroAssignmentParameters.ShiftId)]
    [TestCase(MacroAssignmentParameters.AbsenceTypeId)]
    public void ReadRevert_HolderIdInsteadOfSwitchId_IsRefused(string holderParameter)
    {
        var (request, error) = MacroAssignmentParameters.ReadRevert(
            new Dictionary<string, object> { [holderParameter] = Guid.NewGuid().ToString() });

        request.ShouldBeNull();
        error!.ShouldContain("switchId is required");
    }
}
