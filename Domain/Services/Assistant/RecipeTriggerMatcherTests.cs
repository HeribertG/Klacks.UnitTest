// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the data-driven recipe trigger matcher: a trigger fires only when every allOf
/// condition matches and no noneOf condition matches; word-start stems avoid mid-word false friends;
/// question openers and excluded substrings keep the recipe silent.
/// </summary>

using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTriggerMatcherTests
{
    private static RecipeTrigger AddClientToGroupTrigger() => new()
    {
        AllOf =
        [
            new RecipeCondition { AnyWordStart = ["hinzufüg", "füg", "zuweis", "eintrag", "aufnehm", "zuordn", "add"] },
            new RecipeCondition { AnySubstring = ["gruppe", "team"] }
        ],
        NoneOf =
        [
            new RecipeCondition { StartsWith = ["wie ", "was ", "zeig", "welche"] },
            new RecipeCondition { AnySubstring = ["dienst", "schicht"] }
        ]
    };

    [Test]
    public void Matches_When_AllOf_Present_And_NoneOf_Absent()
    {
        // Arrange
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, "Füge Hans Müller zur Gruppe Bern hinzu");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void Does_Not_Match_When_An_AllOf_Condition_Is_Missing()
    {
        // Arrange — no group/team anchor
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, "Füge Hans Müller hinzu");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void Does_Not_Match_Question_Opener()
    {
        // Arrange
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, "Wie füge ich jemanden zur Gruppe hinzu?");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void Does_Not_Match_When_NoneOf_Substring_Present()
    {
        // Arrange — "dienst" routes to a different recipe, must not match this one
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, "Füge den Dienst zur Gruppe hinzu");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void WordStart_Does_Not_Trigger_On_MidWord_False_Friend()
    {
        // Arrange — "add" must match at a word boundary, not inside "Paddel"
        var trigger = new RecipeTrigger
        {
            AllOf = [new RecipeCondition { AnyWordStart = ["add"] }]
        };

        // Act
        var insideWord = RecipeTriggerMatcher.Matches(trigger, "Das Paddel liegt da");
        var atBoundary = RecipeTriggerMatcher.Matches(trigger, "Please add it");

        // Assert
        Assert.That(insideWord, Is.False);
        Assert.That(atBoundary, Is.True);
    }

    [Test]
    public void Empty_Or_Null_Message_Does_Not_Match()
    {
        // Arrange
        var trigger = AddClientToGroupTrigger();

        // Act & Assert
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null), Is.False);
        Assert.That(RecipeTriggerMatcher.Matches(trigger, "   "), Is.False);
    }

    [Test]
    public void Trigger_With_No_AllOf_Never_Matches()
    {
        // Arrange — a trigger that only excludes must not fire on its own
        var trigger = new RecipeTrigger { NoneOf = [new RecipeCondition { AnySubstring = ["x"] }] };

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, "anything at all");

        // Assert
        Assert.That(result, Is.False);
    }
}
