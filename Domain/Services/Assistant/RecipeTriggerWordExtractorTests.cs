// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the extraction of recipe trigger words to the order and deduplication the knowledge index text
/// was built with before the phrases moved into skill_phrase: per condition anyWordStart, then
/// anySubstring, then startsWith, blanks dropped, duplicates removed case-insensitively keeping the
/// first occurrence, and noneOf never read.
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTriggerWordExtractorTests
{
    [Test]
    public void Extract_ReadsTheThreeListsInOrderPerCondition()
    {
        var trigger = new RecipeTrigger
        {
            AllOf =
            [
                new RecipeCondition
                {
                    StartsWith = ["dritte"],
                    AnySubstring = ["zweite"],
                    AnyWordStart = ["erste"]
                },
                new RecipeCondition { AnyWordStart = ["vierte"] }
            ]
        };

        RecipeTriggerWordExtractor.Extract(trigger).ShouldBe(["erste", "zweite", "dritte", "vierte"]);
    }

    [Test]
    public void Extract_IgnoresNoneOf()
    {
        var trigger = new RecipeTrigger
        {
            AllOf = [new RecipeCondition { AnyWordStart = ["erstell"] }],
            NoneOf = [new RecipeCondition { AnyWordStart = ["loesch"] }]
        };

        RecipeTriggerWordExtractor.Extract(trigger).ShouldBe(["erstell"]);
    }

    [Test]
    public void Extract_DropsBlanksAndCaseInsensitiveDuplicatesKeepingTheFirst()
    {
        var trigger = new RecipeTrigger
        {
            AllOf =
            [
                new RecipeCondition { AnyWordStart = ["Gruppe", "  ", "gruppe"] },
                new RecipeCondition { AnySubstring = ["GRUPPE", "team"] }
            ]
        };

        RecipeTriggerWordExtractor.Extract(trigger).ShouldBe(["Gruppe", "team"]);
    }

    [Test]
    public void Extract_WithoutTrigger_ReturnsEmpty()
    {
        RecipeTriggerWordExtractor.Extract(null).ShouldBeEmpty();
        RecipeTriggerWordExtractor.Extract(new RecipeTrigger()).ShouldBeEmpty();
    }
}
