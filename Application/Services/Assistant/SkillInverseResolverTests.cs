// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The three conditions an undo offer has to satisfy (spec 4.6), one test each: the call succeeded, the
/// call was a write, and the registry declares a LOSSLESS inverse for it. A call that fails any of them
/// yields no offer at all, because a half-true undo is worse than none.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillInverseResolverTests
{
    private const string LosslessWriteSkill = "add_shift_to_group";
    private const string InverseSkill = "remove_shift_from_group";
    private const string ArgumentsJson = """{"shiftId":"s-1","groupId":"g-1"}""";
    private const string ProseOnlyEntrySkill = "place_work";
    private const string UnmappedSkill = "update_client";
    private const string ManualEntrySkill = "create_shift";

    private readonly SkillInverseResolver _resolver = new();

    private static AssistantLastActionCall Call(
        string skillName = LosslessWriteSkill,
        bool success = true,
        bool isReadOnly = false,
        string argumentsJson = ArgumentsJson) => new()
    {
        SkillName = skillName,
        ArgumentsJson = argumentsJson,
        ResultDataJson = "{}",
        IsReadOnly = isReadOnly,
        Success = success
    };

    [Test]
    public void ASuccessfulLosslessWrite_IsResolved()
    {
        _resolver.TryResolve(Call(), out var undo).ShouldBeTrue();

        undo!.SkillName.ShouldBe(InverseSkill);
    }

    [Test]
    public void AFailedCall_IsNotResolved()
    {
        _resolver.TryResolve(Call(success: false), out var undo).ShouldBeFalse();

        undo.ShouldBeNull();
    }

    [Test]
    public void AReadOnlyCall_IsNotResolved()
    {
        _resolver.TryResolve(Call(isReadOnly: true), out _).ShouldBeFalse();
    }

    // A prose entry names the inverse for a human to read; it declares no argument mapping, so nothing
    // can be built from it without guessing.
    [Test]
    public void AProseOnlyEntry_IsNotResolved()
    {
        _resolver.TryResolve(Call(ProseOnlyEntrySkill), out _).ShouldBeFalse();
    }

    [Test]
    public void AManualEntry_IsNotResolved()
    {
        _resolver.TryResolve(Call(ManualEntrySkill), out _).ShouldBeFalse();
    }

    [Test]
    public void AWriteWithoutAnEntry_IsNotResolved()
    {
        _resolver.TryResolve(Call(UnmappedSkill), out _).ShouldBeFalse();
    }

    [Test]
    public void AnIncompleteArgumentSet_IsNotResolved()
    {
        _resolver.TryResolve(Call(argumentsJson: """{"shiftId":"s-1"}"""), out _).ShouldBeFalse();
    }
}
