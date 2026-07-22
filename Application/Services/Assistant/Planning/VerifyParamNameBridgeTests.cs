// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Truth-table unit tests for VerifyParamNameBridge: exact-match short-circuit, the curated
/// employeeId -> clientId alias, the generic-id rule, and the ambiguity case (multiple id-shaped
/// source keys) that must produce no alias plus a note instead of guessing.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Planning;

namespace Klacks.UnitTest.Application.Services.Assistant.Planning;

[TestFixture]
public class VerifyParamNameBridgeTests
{
    private static Dictionary<string, object> Result(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
        {
            dict[key] = value;
        }
        return dict;
    }

    [Test]
    public void ExactCaseInsensitiveMatch_IsTreatedAsSatisfied_NoAlias()
    {
        var result = Result(("ShiftId", "shift-1"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "shiftId" });

        aliases.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }

    [Test]
    public void AliasTable_EmployeeIdBridgesToClientId()
    {
        var employeeId = Guid.NewGuid();
        var result = Result(("EmployeeId", employeeId), ("FirstName", "Ada"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "clientId" });

        aliases.ShouldContainKey("clientId");
        aliases["clientId"].ShouldBe(employeeId);
        notes.ShouldBeEmpty();
    }

    [Test]
    public void AliasTable_ClientIdAlreadyPresent_NoAliasEvenWithEmployeeId()
    {
        var clientId = Guid.NewGuid();
        var result = Result(("EmployeeId", Guid.NewGuid()), ("ClientId", clientId));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "clientId" });

        aliases.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }

    [Test]
    public void GenericId_UniqueIdShapedKey_Bridges()
    {
        var shiftId = Guid.NewGuid();
        var result = Result(("ShiftId", shiftId), ("Name", "Early"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "id" });

        aliases.ShouldContainKey("id");
        aliases["id"].ShouldBe(shiftId);
        notes.ShouldBeEmpty();
    }

    [Test]
    public void GenericId_MultipleIdShapedKeys_IsAmbiguous_NoAliasWithNote()
    {
        var result = Result(("ShiftId", "shift-1"), ("ClientId", "client-1"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "id" });

        aliases.ShouldBeEmpty();
        notes.Count.ShouldBe(1);
        notes[0].ShouldContain("'id'");
        notes[0].ShouldContain("ShiftId");
        notes[0].ShouldContain("ClientId");
    }

    [Test]
    public void NonIdShapedUnsatisfiedParam_IsNotBridged()
    {
        var result = Result(("EmployeeId", "e-1"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "fromDate" });

        aliases.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }

    [Test]
    public void SubstringIdWord_IsNotTreatedAsIdShaped()
    {
        var result = Result(("valid", "true"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, new[] { "id" });

        aliases.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }

    [Test]
    public void NoDeclaredParams_NoAlias()
    {
        var result = Result(("ShiftId", "shift-1"));

        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(result, Array.Empty<string>());

        aliases.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }

    [Test]
    public void EmptyResult_NoAlias()
    {
        var (aliases, notes) = VerifyParamNameBridge.BuildAliases(
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), new[] { "clientId" });

        aliases.ShouldBeEmpty();
        notes.ShouldBeEmpty();
    }
}
