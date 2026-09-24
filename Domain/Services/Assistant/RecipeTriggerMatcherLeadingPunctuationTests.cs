// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Leading punctuation must not hide a startsWith term. MatchesStartsWith used to trim whitespace only, so
/// the Spanish "¿Cómo añado un empleado al grupo?" escaped the es pack veto "cómo " in the keyword path
/// AND in the semantic path (both reach the same IsVetoed), and a quote, bracket or dash in front of a
/// question word did the same for every language. The skipped set is RecipeTriggerLeadingPunctuation;
/// every one of its characters is exercised, the whole-word/open-stem distinction of startsWith terms is
/// re-checked behind punctuation, and a guard makes sure no shipped startsWith term begins with a skipped
/// character (such a term could never match again).
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeTriggerMatcherLeadingPunctuationTests
{
    private const string Spanish = "es";
    private const string German = "de";
    private const string SpanishHowVeto = "cómo ";
    private const string SpanishQuestion = "¿Cómo añado un empleado al grupo?";
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeVetoesFileName = "recipe-vetoes.json";

    private static readonly string[] DefinitionsRelativePath = ["Klacks.Api", "Application", "Skills", "Definitions"];

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly string[] PackLanguages =
    [
        "ar", "cs", "da", "el", "es", "fi", "he", "id", "ja", "ko", "ms",
        "nb", "nl", "pl", "pt", "ro", "sv", "th", "vi", "zh-CN", "zh-TW"
    ];

    private static RecipeTrigger SpanishAddToGroupTrigger() => new()
    {
        AllOf =
        [
            new RecipeCondition { AnyWordStart = ["añad", "add"] },
            new RecipeCondition { AnySubstring = ["grupo", "gruppe"] }
        ]
    };

    private static RecipeTrigger CoreQuestionVetoTrigger() => new()
    {
        AllOf = [new RecipeCondition { AnyWordStart = ["hinzufüg"] }],
        NoneOf = [new RecipeCondition { StartsWith = ["wie ", "zeig"] }]
    };

    public static IEnumerable<TestCaseData> EveryLeadingCharacter() =>
        RecipeTriggerLeadingPunctuation.Characters.Select(c =>
            new TestCaseData(c).SetArgDisplayNames($"U+{(int)c:X4}"));

    [Test]
    public void KeywordPath_SpanishQuestionWithInvertedMark_IsVetoed()
    {
        var trigger = SpanishAddToGroupTrigger();

        RecipeTriggerMatcher.Matches(trigger, null, SpanishQuestion, null, Spanish, [SpanishHowVeto])
            .ShouldBeFalse();
        RecipeTriggerMatcher.Matches(trigger, null, "Añade a Ana al grupo", null, Spanish, [SpanishHowVeto])
            .ShouldBeTrue();
    }

    [Test]
    public void SemanticGuard_SpanishQuestionWithInvertedMark_IsVetoed()
    {
        RecipeTriggerMatcher.IsVetoed(null, SpanishQuestion, null, German, [SpanishHowVeto]).ShouldBeTrue();
        RecipeTriggerMatcher.IsVetoed(null, "Cómo añado un empleado al grupo", null, German, [SpanishHowVeto])
            .ShouldBeTrue();
    }

    [TestCaseSource(nameof(EveryLeadingCharacter))]
    public void EverySkippedCharacter_InFrontOfAQuestionWord_IsVetoed(char leading)
    {
        var message = leading + "Cómo añado un empleado al grupo";

        RecipeTriggerMatcher.IsVetoed(null, message, null, Spanish, [SpanishHowVeto]).ShouldBeTrue();
        RecipeTriggerMatcher.Matches(SpanishAddToGroupTrigger(), null, message, null, Spanish, [SpanishHowVeto])
            .ShouldBeFalse();
    }

    [TestCase("\"Wie lege ich eine Gruppe an?\"")]
    [TestCase("«Wie lege ich eine Gruppe an?»")]
    [TestCase("« Wie lege ich eine Gruppe an ? »")]
    [TestCase("„Wie lege ich eine Gruppe an?“")]
    [TestCase("(wie lege ich eine Gruppe an?)")]
    [TestCase("- wie lege ich eine Gruppe an?")]
    [TestCase("— Wie lege ich eine Gruppe an?")]
    [TestCase("¡¿Wie lege ich eine Gruppe an?!")]
    [TestCase("  \"  Wie lege ich eine Gruppe an?")]
    public void CoreNoneOfQuestionWord_BehindQuoteDashOrParen_IsVetoed(string message)
    {
        RecipeTriggerMatcher.IsVetoed(CoreQuestionVetoTrigger(), message, German).ShouldBeTrue();
    }

    [Test]
    public void NoBreakSpacesBetweenGuillemetAndWord_AreSkippedToo()
    {
        RecipeTriggerMatcher.IsVetoed(CoreQuestionVetoTrigger(), "« Wie geht das »", German).ShouldBeTrue();
    }

    [Test]
    public void OpenStem_BehindPunctuation_KeepsItsPrefixSemantics()
    {
        RecipeTriggerMatcher.IsVetoed(CoreQuestionVetoTrigger(), "\"Zeige mir alle Gruppen\"", German).ShouldBeTrue();
    }

    [TestCase("\"Wiederholung der Gruppe hinzufügen\"")]
    [TestCase("(Wiederholung) der Gruppe hinzufügen")]
    [TestCase("¿Wiederholung?")]
    public void WholeWordTerm_BehindPunctuation_StillDoesNotMatchALongerWord(string message)
    {
        RecipeTriggerMatcher.IsVetoed(CoreQuestionVetoTrigger(), message, German).ShouldBeFalse();
    }

    [TestCase("Füge Anna der Gruppe hinzu")]
    [TestCase("\"Füge Anna der Gruppe hinzu\"")]
    [TestCase("Anna hinzufügen, wie besprochen")]
    [TestCase("Neue Gruppe: \"Wie\"")]
    public void MessagesWithoutALeadingQuestionWord_AreUnaffected(string message)
    {
        RecipeTriggerMatcher.IsVetoed(CoreQuestionVetoTrigger(), message, German).ShouldBeFalse();
    }

    [TestCase("¿?")]
    [TestCase("\"\"")]
    [TestCase("«  »")]
    [TestCase("—")]
    public void MessageOfOnlyPunctuation_IsNeitherVetoedNorMatchedAndDoesNotThrow(string message)
    {
        RecipeTriggerMatcher.IsVetoed(CoreQuestionVetoTrigger(), message, null, German, [SpanishHowVeto])
            .ShouldBeFalse();
        RecipeTriggerMatcher.Matches(SpanishAddToGroupTrigger(), null, message, null, Spanish, [SpanishHowVeto])
            .ShouldBeFalse();
    }

    [Test]
    public void NoShippedStartsWithTerm_BeginsWithASkippedCharacter()
    {
        var offenders = new List<string>();

        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(
            File.ReadAllText(Path.Combine(LocateDirectory(DefinitionsRelativePath), RecipeSeedsFileName)), JsonReadOptions)!;
        var seedTermCount = 0;
        foreach (var recipe in seed.Recipes)
        {
            foreach (var condition in recipe.Trigger.AllOf.Concat(recipe.Trigger.NoneOf))
            {
                foreach (var term in condition.StartsWith ?? [])
                {
                    seedTermCount++;
                    if (BeginsWithSkippedCharacter(term))
                    {
                        offenders.Add($"seed {recipe.Name}: '{term}'");
                    }
                }
            }
        }

        var packTermCount = 0;
        var languagesDirectory = LocateDirectory(PluginsLanguagesRelativePath);
        foreach (var language in PackLanguages)
        {
            var path = Path.Combine(languagesDirectory, language, RecipeVetoesFileName);
            File.Exists(path).ShouldBeTrue($"{language}/{RecipeVetoesFileName} is missing");
            var vetoes = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), JsonReadOptions)!;
            foreach (var (recipe, terms) in vetoes)
            {
                foreach (var term in terms)
                {
                    packTermCount++;
                    if (BeginsWithSkippedCharacter(term))
                    {
                        offenders.Add($"{language} {recipe}: '{term}'");
                    }
                }
            }
        }

        seedTermCount.ShouldBeGreaterThan(0);
        packTermCount.ShouldBeGreaterThan(0);
        offenders.ShouldBeEmpty(
            "a startsWith term that begins with a character RecipeTriggerMatcher skips can never match: "
            + string.Join("; ", offenders));
    }

    private static bool BeginsWithSkippedCharacter(string term) =>
        term.Length > 0
        && (char.IsWhiteSpace(term[0]) || RecipeTriggerLeadingPunctuation.Characters.Contains(term[0]));

    private static string LocateDirectory(string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', relativePath)} from {AppContext.BaseDirectory}");
    }
}
