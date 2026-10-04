// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the preview/apply catalogue: a catalogued skill called with apply=false, apply absent or an
/// unreadable apply value is a preview (the skills read apply with the same reader and treat all of these
/// as false), apply=true in any readable form is not, an uncatalogued skill never is, and every catalogued
/// skill really declares a boolean apply parameter in the seed.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Shouldly;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class PreviewApplySkillCallsTests
{
    private static Dictionary<string, object> Apply(object value) =>
        new() { [PreviewApplySkillCalls.ApplyParameter] = value };

    [Test]
    public void AbsentApply_IsAPreview()
    {
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, new Dictionary<string, object>()).ShouldBeTrue();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, null).ShouldBeTrue();
    }

    [Test]
    public void FalseInEveryForm_IsAPreview()
    {
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply(false)).ShouldBeTrue();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply("false")).ShouldBeTrue();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply(JsonSerializer.SerializeToElement(false))).ShouldBeTrue();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply(JsonSerializer.SerializeToElement("False"))).ShouldBeTrue();
    }

    [Test]
    public void UnreadableApply_IsAPreview_BecauseTheSkillReadsItAsFalseToo()
    {
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply("maybe")).ShouldBeTrue();
    }

    [Test]
    public void TrueInEveryForm_IsNotAPreview()
    {
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply(true)).ShouldBeFalse();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply("true")).ShouldBeFalse();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply("TRUE")).ShouldBeFalse();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply(JsonSerializer.SerializeToElement(true))).ShouldBeFalse();
        PreviewApplySkillCalls.IsPreviewCall(GroupingSkillNames.Apply, Apply(1)).ShouldBeFalse();
    }

    [TestCase("apply_grouping")]
    [TestCase("create_employee")]
    [TestCase("")]
    [TestCase(null)]
    public void UncataloguedSkill_IsNeverAPreview(string? skill)
    {
        PreviewApplySkillCalls.IsPreviewCall(skill, Apply(false)).ShouldBeFalse();
    }

    [Test]
    public void SkillNameMatching_IgnoresCase()
    {
        PreviewApplySkillCalls.IsPreviewCall("Apply_Grouping_Plan", Apply(false)).ShouldBeTrue();
    }

    [Test]
    public void EveryCataloguedSkill_DeclaresABooleanApplyParameterInTheSeed()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(LocateSkillSeeds()));
        var applySkills = doc.RootElement.GetProperty("skills").EnumerateArray()
            .Where(skill => skill.TryGetProperty("parameters", out var parameters)
                && parameters.ValueKind == JsonValueKind.Array
                && parameters.EnumerateArray().Any(parameter =>
                    parameter.TryGetProperty("name", out var name)
                    && name.GetString() == PreviewApplySkillCalls.ApplyParameter
                    && parameter.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "Boolean", StringComparison.OrdinalIgnoreCase)))
            .Select(skill => skill.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        PreviewApplySkillCalls.Skills.ShouldNotBeEmpty();
        PreviewApplySkillCalls.Skills.ShouldAllBe(skill => applySkills.Contains(skill));
    }

    private static string LocateSkillSeeds()
    {
        return RepositoryRootLocator.RequireFile(["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"]);
    }
}
