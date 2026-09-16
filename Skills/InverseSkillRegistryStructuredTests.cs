// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The structured half of InverseSkillRegistry, added for the correction undo offer: argument copying,
/// the id taken from the previous call's result, and the invariants that keep the older prose half
/// intact - a __manual__ entry never yields an undo, no inverse is a read-only skill, and a structured
/// entry never reclassifies its skill as Reversible, which would release it from the autonomy gate.
/// It also keeps the three pairs the spec review rejected as not lossless out of the table: an undo is
/// offered as a restoration of the previous state, so a pair that cannot restore it must not be there.
/// </summary>

using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class InverseSkillRegistryStructuredTests
{
    private static readonly IReadOnlyDictionary<string, string> PairsRejectedAsNotLossless =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["add_client_to_group_by_name"] =
                "the skill reports success as a no-op when the client already was in the group, so the "
                + "undo would remove a membership the corrected turn never created",
            ["assign_contract_by_name"] =
                "removing the assignment does not restore whatever contract state the client had before",
            ["set_shift_required_qualification"] =
                "the call is a modify and may have replaced a previous qualification, which removal does "
                + "not bring back"
        };

    [Test]
    public void APairThatIsNotLossless_StaysOutOfTheRegistry()
    {
        foreach (var (skillName, reason) in PairsRejectedAsNotLossless)
        {
            InverseSkillRegistry.Map.Keys.ShouldNotContain(
                skillName,
                $"{skillName} was rejected by the spec review on 2026-09-16: {reason}. An entry here would "
                + "offer that undo as if it restored the previous state.");
        }
    }

    [Test]
    public void CopiedArguments_AreTakenFromThePreviousCall()
    {
        InverseSkillRegistry.TryBuildUndo(
            "add_shift_to_group",
            """{"shiftId":"s-1","groupId":"g-1","validFrom":"2026-01-01"}""",
            "{}",
            out var undo).ShouldBeTrue();

        undo!.SkillName.ShouldBe("remove_shift_from_group");
        undo.Arguments["shiftId"].ShouldBe("s-1");
        undo.Arguments["groupId"].ShouldBe("g-1");
        undo.Arguments.ShouldNotContainKey("validFrom");
    }

    [Test]
    public void CreatedEntityId_IsTakenFromTheResultData()
    {
        InverseSkillRegistry.TryBuildUndo(
            "create_group",
            """{"name":"Zürich"}""",
            """{"GroupId":"g-9","Name":"Zürich"}""",
            out var undo).ShouldBeTrue();

        undo!.SkillName.ShouldBe("delete_group");
        undo.Arguments["groupId"].ShouldBe("g-9");
    }

    [Test]
    public void MissingResultId_YieldsNoUndo()
    {
        InverseSkillRegistry.TryBuildUndo("create_group", """{"name":"Zürich"}""", "{}", out _).ShouldBeFalse();
    }

    [Test]
    public void MissingCopiedArgument_YieldsNoUndo()
    {
        InverseSkillRegistry.TryBuildUndo(
            "add_shift_to_group", """{"shiftId":"s-1"}""", "{}", out _).ShouldBeFalse();
    }

    // A JSON null is an absent value, not the four letters "null", and an object or array is not an
    // argument value at all. Handing either on would put a literal "null" or a raw JSON fragment into a
    // call that the user would then have to repair - rule 3 says silence instead.
    [Test]
    public void ANullResultId_YieldsNoUndo()
    {
        InverseSkillRegistry.TryBuildUndo(
            "create_group", """{"name":"Zurich"}""", """{"GroupId":null}""", out _).ShouldBeFalse();
    }

    [Test]
    public void AStructuredArgumentValue_YieldsNoUndo()
    {
        InverseSkillRegistry.TryBuildUndo(
            "add_shift_to_group",
            """{"shiftId":{"id":"s-1"},"groupId":"g-1"}""",
            "{}",
            out _).ShouldBeFalse();
    }

    [Test]
    public void ANumericResultId_IsStillRead()
    {
        InverseSkillRegistry.TryBuildUndo(
            "create_group", """{"name":"Zurich"}""", """{"GroupId":42}""", out var undo).ShouldBeTrue();

        undo!.Arguments["groupId"].ShouldBe("42");
    }

    [Test]
    public void ManualEntry_YieldsNoUndo()
    {
        InverseSkillRegistry.TryGet("create_shift", out var entry).ShouldBeTrue();
        entry.SkillName.ShouldBe(InverseSkillRegistry.ManualMarker);

        InverseSkillRegistry.TryBuildUndo(
            "create_shift", """{"name":"x"}""", """{"ShiftId":"s-1"}""", out _).ShouldBeFalse();
    }

    [Test]
    public void UnknownSkill_YieldsNoUndo()
    {
        InverseSkillRegistry.TryBuildUndo("no_such_skill", "{}", "{}", out _).ShouldBeFalse();
    }

    [Test]
    public void EveryStructuredEntry_PointsAtAWriteSkill()
    {
        foreach (var (_, entry) in InverseSkillRegistry.Map)
        {
            if (entry.CopiedArguments.Count == 0 && entry.ResultIdProperty == null)
            {
                continue;
            }

            ReadOnlySkillPrefixes.HasReadOnlyPrefix(entry.SkillName).ShouldBeFalse(
                $"the inverse of a write must itself be a write, but '{entry.SkillName}' reads as read-only");
            entry.SkillName.ShouldNotBe(InverseSkillRegistry.ManualMarker);
        }
    }

    [Test]
    public void EveryStructuredEntry_IsUndoOnly_SoNoSkillLeavesTheAutonomyGate()
    {
        foreach (var (skillName, entry) in InverseSkillRegistry.Map)
        {
            if (entry.CopiedArguments.Count == 0 && entry.ResultIdProperty == null)
            {
                continue;
            }

            entry.UndoOnly.ShouldBeTrue(
                $"{skillName} declares a structured inverse; without UndoOnly it would turn Reversible and "
                + "the autonomy gate would stop holding it for confirmation, which needs owner approval (spec §8)");
        }
    }

    [Test]
    public void UndoOnlyEntry_StillYieldsAnUndo_ButIsNotClassifiedReversible()
    {
        InverseSkillRegistry.TryBuildUndo(
            "add_shift_to_group", """{"shiftId":"s-1","groupId":"g-1"}""", "{}", out var undo).ShouldBeTrue();
        undo.ShouldNotBeNull();

        new SkillRiskClassifier()
            .Classify(new SkillDescriptor("add_shift_to_group", string.Empty, SkillCategory.Crud, [], [], [], null))
            .ShouldNotBe(SkillRiskClass.Reversible);
    }

    [Test]
    public void EveryStructuredEntry_DeclaresArgumentsOrAResultId_NeverBoth()
    {
        foreach (var (skillName, entry) in InverseSkillRegistry.Map)
        {
            if (entry.ResultIdProperty == null)
            {
                continue;
            }

            entry.ResultIdArgument.ShouldNotBeNull($"{skillName} names a result id property but no target argument");
            entry.CopiedArguments.ShouldBeEmpty(
                $"{skillName} mixes copied arguments with a result id; the undo shape is one or the other");
        }
    }
}
