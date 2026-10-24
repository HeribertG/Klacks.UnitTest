// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A recipe that engages on an item expecting none marks the item Excluded, which removes it from every accuracy
/// dimension - a rising hijack rate only showed as a smaller denominator. UnexpectedRecipeRate makes it visible, and
/// the per-locale aggregation keeps a regression in one language from being averaged away by the German majority.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalScorerUnexpectedRecipeTests
{
    [Test]
    public void Aggregate_CountsEngagedRecipesOnItemsThatExpectNone_IncludingExcludedOnes()
    {
        var dimensions = TurnEvalScorer.Aggregate(
        [
            Item("a", engineTriggers: true, excluded: true),
            Item("b"),
            Item("c"),
            Item("d", forces: true, excluded: true)
        ]);

        dimensions.UnexpectedRecipeRate.ShouldBe(0.5);
        dimensions.ItemsExcluded.ShouldBe(2);
    }

    [Test]
    public void Aggregate_IgnoresRecipeItemsAndErroredItems()
    {
        var dimensions = TurnEvalScorer.Aggregate(
        [
            Item("recipe", engineTriggers: true, expectedRecipe: "create-group"),
            Item("errored", engineTriggers: true, errored: true),
            Item("clean")
        ]);

        dimensions.UnexpectedRecipeRate.ShouldBe(0.0);
    }

    [Test]
    public void Aggregate_WithOnlyRecipeItems_LeavesTheRateUnmeasured()
    {
        var dimensions = TurnEvalScorer.Aggregate([Item("recipe", expectedRecipe: "create-group")]);

        dimensions.UnexpectedRecipeRate.ShouldBeNull();
    }

    [Test]
    public void AggregateByLocale_GroupsPerLocale_AndCollectsMissingLocalesUnderUnknown()
    {
        var byLocale = TurnEvalScorer.AggregateByLocale(
        [
            Item("de-1", locale: "de"),
            Item("de-2", locale: "de", engineTriggers: true, excluded: true),
            Item("pl-1", locale: "pl"),
            Item("none", locale: null)
        ]);

        byLocale.Keys.ShouldBe(["de", "pl", TurnEvalScorer.UnknownLocale], ignoreOrder: true);
        byLocale["de"].ItemsTotal.ShouldBe(2);
        byLocale["de"].UnexpectedRecipeRate.ShouldBe(0.5);
        byLocale["pl"].UnexpectedRecipeRate.ShouldBe(0.0);
        byLocale[TurnEvalScorer.UnknownLocale].ItemsTotal.ShouldBe(1);
    }

    private static TurnEvalItemResult Item(
        string id,
        bool engineTriggers = false,
        bool forces = false,
        bool excluded = false,
        bool errored = false,
        string? expectedRecipe = null,
        string? locale = "de") => new()
    {
        ItemId = id,
        ExpectedRecipe = expectedRecipe,
        EngineRecipeWouldTrigger = engineTriggers,
        RecipeWouldForce = forces,
        Excluded = excluded,
        Errored = errored,
        Locale = locale,
        Passed = !excluded && !errored
    };
}
