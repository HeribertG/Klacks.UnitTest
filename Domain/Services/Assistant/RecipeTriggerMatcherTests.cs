// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the data-driven recipe trigger matcher: a trigger fires only when every allOf
/// condition matches and no noneOf condition matches; word-start stems avoid mid-word false friends;
/// question openers and excluded substrings keep the recipe silent.
/// </summary>

using Klacks.Api.Domain.Constants;
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

    [TestCase("Wie?")]
    [TestCase("Wie!")]
    [TestCase("Wie geht das")]
    public void StartsWith_WholeWordLead_VetoesTheQuestion_EvenWithoutAFollowingSpace(string message)
    {
        // Arrange — "wie " carries a trailing space, which marks it as a whole word rather than a stem.
        // A plain prefix compare needed a literal space and therefore let the one-word question through.
        var trigger = new RecipeTrigger
        {
            NoneOf = [new RecipeCondition { StartsWith = ["wie "] }]
        };

        // Act
        var result = RecipeTriggerMatcher.IsVetoed(trigger, message);

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void StartsWith_WholeWordLead_DoesNotVetoALongerWordStartingWithIt()
    {
        // Arrange — the word boundary must not turn the lead into an open stem.
        var trigger = new RecipeTrigger
        {
            NoneOf = [new RecipeCondition { StartsWith = ["wie "] }]
        };

        // Act
        var result = RecipeTriggerMatcher.IsVetoed(trigger, "Wiederholung der Dienste anlegen");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void StartsWith_TermWithoutATrailingSpace_StaysAnOpenStem()
    {
        // Arrange — the seeded recipes rely on "zeig" covering "zeige"; only the trailing space switches
        // a term to whole-word matching.
        var trigger = new RecipeTrigger
        {
            NoneOf = [new RecipeCondition { StartsWith = ["zeig"] }]
        };

        // Act & Assert
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "Zeige mir die Gruppen"), Is.True);
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "Zeig die Gruppen"), Is.True);
    }

    [Test]
    public void StartsWith_MultiWordLead_KeepsItsInnerSpaces()
    {
        // Arrange
        var trigger = new RecipeTrigger
        {
            NoneOf = [new RecipeCondition { StartsWith = ["gibt es "] }]
        };

        // Act & Assert
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "Gibt es?"), Is.True);
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "Gibt es offene Dienste"), Is.True);
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "Gibt eskalation frei"), Is.False);
    }

    [Test]
    public void QuestionLeads_VetoEveryOneWordQuestionOfTheCoreLanguages()
    {
        // Arrange — the list RecipeDraftValidator writes into every generated recipe's noneOf.
        var trigger = new RecipeTrigger
        {
            NoneOf = [new RecipeCondition { StartsWith = [.. RecipeQuestionLeads.All] }]
        };

        // Act & Assert
        foreach (var question in new[] { "Wie?", "Wer?", "Warum?", "Was?", "How?", "Who?", "Qui?", "Chi?" })
        {
            Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, question), Is.True, question);
        }
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

    [Test]
    public void Synonym_Fires_When_AllOf_Does_Not_Match()
    {
        // Arrange — a Spanish message that does not satisfy the German allOf, but a plugin-language synonym
        var trigger = AddClientToGroupTrigger();
        var message = "incorporar un empleado al grupo";
        string[] synonyms = ["incorporar un empleado al grupo"];

        // Act
        var withoutSynonyms = RecipeTriggerMatcher.Matches(trigger, message);
        var withSynonyms = RecipeTriggerMatcher.Matches(trigger, synonyms, message);

        // Assert
        Assert.That(withoutSynonyms, Is.False, "no German allOf hit, so the core path stays silent");
        Assert.That(withSynonyms, Is.True, "the plugin-language synonym fires the recipe");
    }

    [Test]
    public void Synonym_Is_Still_Blocked_By_NoneOf()
    {
        // Arrange — a question opener must keep the recipe silent even when a synonym is present
        var trigger = AddClientToGroupTrigger();
        string[] synonyms = ["incorporar un empleado al grupo"];

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, synonyms, "wie incorporar un empleado al grupo?");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void Synonym_Overload_With_No_Synonyms_Equals_Core_Path()
    {
        // Arrange — the language-scoped overload must be a pure no-op for core languages (null/empty synonyms)
        var trigger = AddClientToGroupTrigger();
        var match = "Füge Hans zur Gruppe Bern hinzu";
        var noMatch = "incorporar un empleado al grupo";

        // Act & Assert — identical verdict to the 2-arg path in both directions. The synonyms argument is
        // named on purpose: with a positional null the (trigger, message, language) overload is the better
        // match, so the message lands in `language` and Matches returns false regardless of the trigger.
        // This test compares overload equivalence, so it has to bind the overload it means.
        Assert.That(RecipeTriggerMatcher.Matches(trigger, synonyms: null, message: match),
            Is.EqualTo(RecipeTriggerMatcher.Matches(trigger, match)));
        Assert.That(RecipeTriggerMatcher.Matches(trigger, [], match),
            Is.EqualTo(RecipeTriggerMatcher.Matches(trigger, match)));
        Assert.That(RecipeTriggerMatcher.Matches(trigger, synonyms: null, message: noMatch),
            Is.EqualTo(RecipeTriggerMatcher.Matches(trigger, noMatch)));
        Assert.That(RecipeTriggerMatcher.Matches(trigger, [], noMatch),
            Is.EqualTo(RecipeTriggerMatcher.Matches(trigger, noMatch)));
    }

    [Test]
    public void Positional_Null_Synonyms_Must_Evaluate_The_Message_Not_Bind_It_To_A_Language()
    {
        // Arrange — regression lock for the overload trap, and the one the named-argument tests above
        // cannot provide: naming `synonyms:` is precisely what keeps those calls on the right overload, so
        // they stay green even if the trap returns. With three positional arguments and a null in the
        // second slot the call must reach the synonyms overload and evaluate the message. Should the short
        // overload ever regain an optional parameter, this binds (message: null, language: text) instead
        // and Matches returns false for every trigger - which is how the live regression guard for the
        // 2026-07-16 company-rule incident was silently disarmed.
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, null, "Füge Hans zur Gruppe Bern hinzu");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void Synonym_Does_Not_Override_AllOf_For_Core_Language_Message()
    {
        // Arrange — a German message still matches via allOf regardless of (irrelevant) synonyms
        var trigger = AddClientToGroupTrigger();
        string[] synonyms = ["incorporar un empleado al grupo"];

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, synonyms, "Füge Hans zur Gruppe Bern hinzu");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsVetoed_When_A_NoneOf_Condition_Matches()
    {
        // Arrange
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.IsVetoed(trigger, "Füge den Dienst zur Gruppe Bern hinzu");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void IsVetoed_Is_False_When_No_NoneOf_Condition_Matches()
    {
        // Arrange
        var trigger = AddClientToGroupTrigger();

        // Act
        var result = RecipeTriggerMatcher.IsVetoed(trigger, "Füge Hans zur Gruppe Bern hinzu");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsVetoed_Is_False_For_Null_Trigger_Or_Blank_Message()
    {
        // Arrange
        var trigger = AddClientToGroupTrigger();

        // Act & Assert
        Assert.That(RecipeTriggerMatcher.IsVetoed(null, "Füge den Dienst hinzu"), Is.False);
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "  "), Is.False);
    }

    // AnyWordStartByLocale exists because 'alle' is a German plural article but an Italian and Finnish
    // preposition: the unanchored substring 'alle ' vetoed every Italian message naming a time range and
    // every Finnish move-group synonym. The branch is skipped when no language is passed, so these are the
    // only tests that prove it works - a gate calling the matcher language-agnostically cannot see it.
    private static RecipeTrigger BulkMarkerTrigger() => new()
    {
        AllOf =
        [
            new RecipeCondition
            {
                AnyWordStartByLocale = new Dictionary<string, List<string>> { ["de"] = ["alle"] }
            },
            new RecipeCondition { AnySubstring = ["mitarbeiter"] }
        ]
    };

    private static RecipeTrigger SingleRecipeVetoedByBulkMarkerTrigger() => new()
    {
        AllOf = [new RecipeCondition { AnySubstring = ["mitarbeiter"] }],
        NoneOf =
        [
            new RecipeCondition
            {
                AnyWordStartByLocale = new Dictionary<string, List<string>> { ["de"] = ["alle"] }
            }
        ]
    };

    [Test]
    public void Locale_Stem_Fires_When_The_Detected_Language_Matches_The_Key()
    {
        // Arrange
        var trigger = BulkMarkerTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, null, "alle Mitarbeiter zur Gruppe Bern", "de");

        // Assert
        Assert.That(result, Is.True);
    }

    [Test]
    public void Locale_Stem_Does_Not_Fire_For_Any_Other_Detected_Language()
    {
        // Arrange — identical surface form, but the message is not German
        var trigger = BulkMarkerTrigger();

        // Act & Assert
        foreach (var language in new[] { "it", "fi", "fr", "en", "es" })
        {
            Assert.That(
                RecipeTriggerMatcher.Matches(trigger, null, "alle Mitarbeiter zur Gruppe Bern", language),
                Is.False,
                $"a 'de'-scoped stem must not fire for detected language '{language}'");
        }
    }

    [Test]
    public void Locale_Stem_Is_Skipped_When_No_Language_Is_Detected()
    {
        // Arrange — the language-less overload is the backward-compatible path
        var trigger = BulkMarkerTrigger();

        // Act
        var result = RecipeTriggerMatcher.Matches(trigger, "alle Mitarbeiter zur Gruppe Bern");

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void Locale_Key_Comparison_Is_Case_Insensitive()
    {
        // Arrange — the matcher compares keys OrdinalIgnoreCase, matching SynonymsFor's convention
        var upperKey = new RecipeTrigger
        {
            AllOf =
            [
                new RecipeCondition
                {
                    AnyWordStartByLocale = new Dictionary<string, List<string>> { ["DE"] = ["alle"] }
                }
            ]
        };
        var lowerKey = new RecipeTrigger
        {
            AllOf =
            [
                new RecipeCondition
                {
                    AnyWordStartByLocale = new Dictionary<string, List<string>> { ["de"] = ["alle"] }
                }
            ]
        };

        // Act & Assert — either side may carry the casing
        Assert.That(RecipeTriggerMatcher.Matches(upperKey, null, "alle Mitarbeiter", "de"), Is.True);
        Assert.That(RecipeTriggerMatcher.Matches(lowerKey, null, "alle Mitarbeiter", "DE"), Is.True);
    }

    [Test]
    public void Locale_Stem_Still_Anchors_At_A_Word_Boundary()
    {
        // Arrange — the locale list reuses MatchesWordStart, so it inherits the \b anchoring
        var trigger = new RecipeTrigger
        {
            AllOf =
            [
                new RecipeCondition
                {
                    AnyWordStartByLocale = new Dictionary<string, List<string>> { ["de"] = ["alle"] }
                }
            ]
        };

        // Act & Assert — German inflections fire, a mid-word homograph does not
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "alle Mitarbeiter", "de"), Is.True);
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "allen Mitarbeitern", "de"), Is.True);
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "alles prüfen", "de"), Is.True);
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "die Falle prüfen", "de"), Is.False);
    }

    [Test]
    public void Locale_Stem_Is_Or_Combined_With_Language_Neutral_AnyWordStart()
    {
        // Arrange — one condition carrying both lists
        var trigger = new RecipeTrigger
        {
            AllOf =
            [
                new RecipeCondition
                {
                    AnyWordStart = ["gruppe"],
                    AnyWordStartByLocale = new Dictionary<string, List<string>> { ["de"] = ["alle"] }
                }
            ]
        };

        // Act & Assert — the neutral stem fires for any language, and with no language at all
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "zur Gruppe Bern", "it"), Is.True);
        Assert.That(RecipeTriggerMatcher.Matches(trigger, "zur Gruppe Bern"), Is.True);

        // ...while the locale stem still only fires for its own language
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "alle Mitarbeiter", "de"), Is.True);
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "alle Mitarbeiter", "it"), Is.False);
    }

    [Test]
    public void Locale_Scoping_Stops_The_Italian_And_Finnish_Bulk_Marker_False_Veto()
    {
        // Arrange — the seeded shape: 'alle' vetoes the single recipe so a bulk request cannot start it
        var trigger = SingleRecipeVetoedByBulkMarkerTrigger();

        // Act & Assert — German still vetoes
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "alle Mitarbeiter anlegen", "de"), Is.True);

        // The two homographs that used to veto through the unanchored 'alle ' substring no longer do.
        // Italian time ranges are ubiquitous in shift planning; Finnish 'alle' means 'under'.
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "dalle 7 alle 15", "it"), Is.False);
        Assert.That(RecipeTriggerMatcher.IsVetoed(trigger, "siirrä ryhmä alle viikon", "fi"), Is.False);

        // And the recipe is reachable again for exactly those messages
        Assert.That(RecipeTriggerMatcher.Matches(trigger, null, "dalle 7 alle 15 mitarbeiter", "it"), Is.True);
    }
}
