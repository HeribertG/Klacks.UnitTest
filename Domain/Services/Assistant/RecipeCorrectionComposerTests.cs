// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// RecipeCorrectionComposer is the single source for the composite a re-resolve runs on, and the
/// byte-identity is load-bearing rather than cosmetic: RecipeEngineService memoizes on
/// (message, language, excluded), so the toolset assembler and the correction branch share one embedding
/// round only if they compose identically. Two different separators would not merely pay twice - the two
/// callers could resolve different recipes, guaranteeing one recipe's skills for a turn that then runs
/// another.
///
/// The cap is enforced here rather than only in the column configuration because EF InMemory ignores
/// HasMaxLength, so a store test cannot prove the constraint. Persist writes this value on every ask-pause.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeCorrectionComposerTests
{
    private const string Trigger = "Verteile alle externen Mitarbeiter auf die für sie nächsten Gruppen";
    private const string Correction = "Nein du hast mich missverstanden, alle Mitarbeitern, Externen und Kunden";

    [Test]
    public void Compose_JoinsTriggerAndCorrectionWithTheSharedSeparator()
    {
        RecipeCorrectionComposer.Compose(Trigger, Correction)
            .ShouldBe(Trigger + RecipeEngineDefaults.CorrectionCompositeSeparator + Correction);
    }

    /// <summary>
    /// A pending row written before the column existed, and any caller that has no plan. Falling back to
    /// the correction alone keeps the pre-stage-2 behaviour visible instead of inventing intent - and that
    /// fallback usually resolves to nothing, which is the honest outcome for a message that opens with a
    /// negation and carries no mutation verb.
    /// </summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Compose_WithoutAPersistedTrigger_FallsBackToTheCorrectionAlone(string? trigger)
    {
        RecipeCorrectionComposer.Compose(trigger, Correction).ShouldBe(Correction);
    }

    [Test]
    public void Compose_IsDeterministicSoBothCallersHitOneMemoEntry()
    {
        RecipeCorrectionComposer.Compose(Trigger, Correction)
            .ShouldBe(RecipeCorrectionComposer.Compose(Trigger, Correction));
    }

    [Test]
    public void CapForStorage_TrimsWhitespace()
    {
        RecipeCorrectionComposer.CapForStorage("  " + Trigger + "  ").ShouldBe(Trigger);
    }

    [Test]
    public void CapForStorage_TruncatesToTheColumnLimit()
    {
        var tooLong = new string('x', RecipeEngineDefaults.PendingRecipeTriggerMessageMaxLength + 50);

        RecipeCorrectionComposer.CapForStorage(tooLong)!
            .Length.ShouldBe(RecipeEngineDefaults.PendingRecipeTriggerMessageMaxLength);
    }

    /// <summary>
    /// Null rather than an empty string, so "nothing was persisted" stays distinguishable from "the
    /// triggering message was empty" - Compose treats them the same, but a store roundtrip test should not
    /// have to guess which one it stored.
    /// </summary>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void CapForStorage_OfNothingIsNull(string? trigger)
    {
        RecipeCorrectionComposer.CapForStorage(trigger).ShouldBeNull();
    }
}
