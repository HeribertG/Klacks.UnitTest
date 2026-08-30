// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the recipe-trigger veto vocabulary (W3.6): every recipe must carry the full core-language
/// question-word veto in its noneOf startsWith block, so an information question ("How do I create an
/// employee?") can never deterministically start the mutation recipe. The DE/fr/it part was fixed
/// 2026-08-28; the EN part and the fr/it copula veto were added 2026-08-30 (see
/// docs/knowledge/klacksy-recipe-trigger-noneof-question-word-gap-2026-08-28.md).
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeTriggerVetoQualityTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] RequiredEnglishQuestionVeto =
    [
        "how ", "what ", "why ", "when ", "where ", "who ", "whom ", "which ", "show ", "explain", "is there"
    ];

    private static readonly string[] RequiredCopulaVeto =
    [
        "est-ce", "c'est", "cos'è", "è "
    ];

    [Test]
    public void EveryRecipe_MustVetoEnglishQuestionWords()
    {
        var violations = new List<string>();

        foreach (var (name, trigger) in LoadTriggers())
        {
            var startsWith = ReadStartsWithTerms(trigger);
            var missing = RequiredEnglishQuestionVeto.Where(required => !startsWith.Contains(required)).ToList();
            if (missing.Count > 0)
            {
                violations.Add($"{name}: missing EN question veto {string.Join(", ", missing)}");
            }
        }

        violations.ShouldBeEmpty(
            $"{RecipeSeedsFileName} contains recipes without the English question-word veto; an EN " +
            "information question could deterministically start a mutation recipe. Offenders: " +
            string.Join("; ", violations));
    }

    [Test]
    public void EveryRecipe_MustVetoFrenchAndItalianCopulaQuestions()
    {
        var violations = new List<string>();

        foreach (var (name, trigger) in LoadTriggers())
        {
            var startsWith = ReadStartsWithTerms(trigger);
            var missing = RequiredCopulaVeto.Where(required => !startsWith.Contains(required)).ToList();
            if (missing.Count > 0)
            {
                violations.Add($"{name}: missing fr/it copula veto {string.Join(", ", missing)}");
            }
        }

        violations.ShouldBeEmpty(
            $"{RecipeSeedsFileName} contains recipes without the fr/it copula veto. Offenders: " +
            string.Join("; ", violations));
    }

    private static HashSet<string> ReadStartsWithTerms(RecipeTrigger trigger)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in trigger.NoneOf)
        {
            if (condition.StartsWith is { Count: > 0 })
            {
                foreach (var term in condition.StartsWith)
                {
                    terms.Add(term);
                }
            }
        }

        return terms;
    }

    private static List<(string Name, RecipeTrigger Trigger)> LoadTriggers()
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(LocateDefinitionsFile(RecipeSeedsFileName)));
        var result = new List<(string, RecipeTrigger)>();

        foreach (var element in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString() ?? string.Empty;
            if (!element.TryGetProperty("trigger", out var triggerElement))
            {
                continue;
            }

            var trigger = JsonSerializer.Deserialize<RecipeTrigger>(triggerElement.GetRawText(), jsonOptions);
            if (trigger != null)
            {
                result.Add((name, trigger));
            }
        }

        return result;
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(DefinitionsRelativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', DefinitionsRelativePath)}/{fileName} by walking up from the test base directory.");
    }
}
