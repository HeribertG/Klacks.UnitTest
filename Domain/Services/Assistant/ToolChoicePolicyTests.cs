// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Full truth-table tests for ToolChoicePolicy — the mutation-guard tool_choice predicate extracted
/// from both chat loops. Locks the exact behaviour: a tool call is forced when a recipe step forces it,
/// or when a mutation / navigation / confirmation intent is present and no tool has run yet this turn.
/// Also verifies the wire mapping (required vs null, never the literal "auto").
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ToolChoicePolicyTests
{
    [Test]
    public void ShouldForceToolCall_MatchesFullTruthTable()
    {
        foreach (var forceRecipe in Bools)
        foreach (var isMutationIntent in Bools)
        foreach (var isNavigationIntent in Bools)
        foreach (var forceConfirmation in Bools)
        foreach (var toolCallCount in Counts)
        {
            var actual = ToolChoicePolicy.ShouldForceToolCall(
                forceRecipe, isMutationIntent, isNavigationIntent, forceConfirmation, toolCallCount);

            var expected = forceRecipe
                || ((isMutationIntent || isNavigationIntent || forceConfirmation) && toolCallCount == 0);

            actual.ShouldBe(expected,
                $"fR={forceRecipe} M={isMutationIntent} N={isNavigationIntent} C={forceConfirmation} count={toolCallCount}");
        }
    }

    [TestCase(false, false, false, false, 0, false)]
    [TestCase(false, true, false, false, 0, true)]
    [TestCase(false, false, true, false, 0, true)]
    [TestCase(false, false, false, true, 0, true)]
    [TestCase(false, true, false, false, 1, false)]
    [TestCase(false, false, true, false, 1, false)]
    [TestCase(false, false, false, true, 1, false)]
    [TestCase(true, false, false, false, 0, true)]
    [TestCase(true, false, false, false, 1, true)]
    [TestCase(true, true, true, true, 1, true)]
    public void ShouldForceToolCall_RepresentativeRows(
        bool forceRecipe, bool isMutationIntent, bool isNavigationIntent, bool forceConfirmation,
        int toolCallCount, bool expected)
    {
        ToolChoicePolicy.ShouldForceToolCall(
            forceRecipe, isMutationIntent, isNavigationIntent, forceConfirmation, toolCallCount)
            .ShouldBe(expected);
    }

    [Test]
    public void ResolveToolChoice_ReturnsRequired_WhenForced()
    {
        ToolChoicePolicy.ResolveToolChoice(
            forceRecipe: false, isMutationIntent: true, isNavigationIntent: false,
            forceConfirmation: false, toolCallCount: 0)
            .ShouldBe(MutationGuardConstants.ToolChoiceRequired);
    }

    [Test]
    public void ResolveToolChoice_ReturnsNull_WhenNotForced()
    {
        ToolChoicePolicy.ResolveToolChoice(
            forceRecipe: false, isMutationIntent: false, isNavigationIntent: false,
            forceConfirmation: false, toolCallCount: 0)
            .ShouldBeNull();
    }

    [Test]
    public void ResolveToolChoice_DelegatesToPredicate_AcrossFullTruthTable()
    {
        foreach (var forceRecipe in Bools)
        foreach (var isMutationIntent in Bools)
        foreach (var isNavigationIntent in Bools)
        foreach (var forceConfirmation in Bools)
        foreach (var toolCallCount in Counts)
        {
            var forced = ToolChoicePolicy.ShouldForceToolCall(
                forceRecipe, isMutationIntent, isNavigationIntent, forceConfirmation, toolCallCount);
            var resolved = ToolChoicePolicy.ResolveToolChoice(
                forceRecipe, isMutationIntent, isNavigationIntent, forceConfirmation, toolCallCount);

            resolved.ShouldBe(forced ? MutationGuardConstants.ToolChoiceRequired : null);
        }
    }

    private static readonly bool[] Bools = [false, true];
    private static readonly int[] Counts = [0, 1];
}
