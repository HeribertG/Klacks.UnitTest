// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Truth-table tests for ForceToolNudgePolicy — the non-streaming tool-forcing retry trigger.
/// Nails down today's condition so a future refactor cannot silently shift the deliberate streaming/
/// non-streaming asymmetry, and proves the added completion-claim term is purely additive: with
/// claimsCompletion=false the result is identical to the legacy inline condition.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ForceToolNudgePolicyTests
{
    [Test]
    public void ShouldForceToolNudge_MatchesTruthTable_And_PreservesLegacyWhenNoClaim()
    {
        foreach (var isMutationIntent in Bools)
        foreach (var forceConfirmation in Bools)
        foreach (var containsMarkup in Bools)
        foreach (var claimsCompletion in Bools)
        foreach (var toolCallCount in Counts)
        foreach (var recipePausedOnAsk in Bools)
        foreach (var isClarifyingResponse in Bools)
        {
            var actual = ForceToolNudgePolicy.ShouldForceToolNudge(
                isMutationIntent, forceConfirmation, containsMarkup, claimsCompletion,
                toolCallCount, recipePausedOnAsk, isClarifyingResponse);

            var signals = isMutationIntent || forceConfirmation || containsMarkup || claimsCompletion;
            var expected = signals && toolCallCount == 0 && !recipePausedOnAsk && !isClarifyingResponse;

            var label =
                $"M={isMutationIntent} conf={forceConfirmation} markup={containsMarkup} claim={claimsCompletion} " +
                $"count={toolCallCount} paused={recipePausedOnAsk} clar={isClarifyingResponse}";
            actual.ShouldBe(expected, label);

            if (!claimsCompletion)
            {
                var legacy = (isMutationIntent || forceConfirmation || containsMarkup)
                    && toolCallCount == 0 && !recipePausedOnAsk && !isClarifyingResponse;
                actual.ShouldBe(legacy, "additive term must not change behaviour when claimsCompletion=false: " + label);
            }
        }
    }

    [TestCase(false, false, false, false, 0, false, false, false)]
    [TestCase(true, false, false, false, 0, false, false, true)]
    [TestCase(false, true, false, false, 0, false, false, true)]
    [TestCase(false, false, true, false, 0, false, false, true)]
    [TestCase(false, false, false, true, 0, false, false, true)]
    [TestCase(false, false, false, true, 1, false, false, false)]
    [TestCase(false, false, false, true, 0, true, false, false)]
    [TestCase(false, false, false, true, 0, false, true, false)]
    [TestCase(true, false, false, false, 1, false, false, false)]
    public void ShouldForceToolNudge_RepresentativeRows(
        bool isMutationIntent, bool forceConfirmation, bool containsMarkup, bool claimsCompletion,
        int toolCallCount, bool recipePausedOnAsk, bool isClarifyingResponse, bool expected)
    {
        ForceToolNudgePolicy.ShouldForceToolNudge(
            isMutationIntent, forceConfirmation, containsMarkup, claimsCompletion,
            toolCallCount, recipePausedOnAsk, isClarifyingResponse)
            .ShouldBe(expected);
    }

    private static readonly bool[] Bools = [false, true];
    private static readonly int[] Counts = [0, 1];
}
