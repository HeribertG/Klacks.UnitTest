// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Cross-language collision gate for the recipe trigger vocabulary (25 languages).
///
/// The structured trigger vocabulary in recipe-seeds.json is authored in the four core languages
/// (de/en/fr/it), but RecipeTriggerMatcher runs it against EVERY message regardless of the detected
/// language: anySubstring is a plain unanchored message.Contains(term, OrdinalIgnoreCase). A core-language
/// term can therefore match mid-word inside a word of one of the 21 plugin languages - or of another core
/// language - and silently widen an allOf or wrongly fire a noneOf veto. This is the documented
/// "endre inside prendre" class (DevKnowledge 2026-06-30, Cross-Language-Kollisionsrisiko), which until
/// now was only ever found by hand, after the fact.
///
/// The gate covers the subclass that needs NO semantic judgement to decide: a term written WITH a trailing
/// space. That space is the author's own marker for "this is a whole word, not a stem" (see
/// RecipeTriggerMatcher.MatchesStartsWith), yet anySubstring does not enforce it. So a space-suffixed term
/// matching mid-word is provably against author intent, in every language, and the build should be red.
/// Terms without a trailing space are deliberately out of scope: for those, a mid-word hit is often the
/// intended German/Dutch compound behaviour ("dienst" inside "Frühdienst"), which only a human can triage.
///
/// Corpus = real per-language text: the 21 plugin recipe-synonyms.json packs (authored as user utterances),
/// the goldset messages, and the de/en/fr/it goal and ask-prompt translations.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeTriggerWholeWordCollisionTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeSynonymsFileName = "recipe-synonyms.json";
    private const string TurnGoldsetFileName = "turn-selection-v1.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] GoldsetsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Goldsets"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    /// <summary>
    /// Every space-suffixed anySubstring term that is currently known to match mid-word, with the reason it
    /// stays. Following the convention of VetoExemptRecipes / AskStepAllowlist / IntendedOverlaps: one
    /// reviewable entry per exception, and a debt entry names its proposed fix instead of blessing it.
    /// </summary>
    private static readonly Dictionary<string, string> AcceptedMidWordMatches = new(StringComparer.Ordinal)
    {
        ["plan "] =
            "Accepted, no debt. German-internal compounds only (Einsatzplan/Dienstplan/Ferienplan); " +
            "create-shift-order is deliberately vetoed by plan nouns. No plugin-language hit.",
        ["typ "] =
            "Accepted, no debt. German-internal compound only (Personentyp); move/remove-absence are " +
            "deliberately vetoed by it. No plugin-language hit.",
    };

    /// <summary>
    /// The customer-domain veto recipe-authoring.md §2 mandates as a sibling-intent guard. Both members of
    /// the external-employee nearest-group pair must carry all of them: the bulk sibling always did, the
    /// single recipe did not, which is the gap this locks shut.
    /// </summary>
    private static readonly string[] CustomerDomainVeto =
    [
        "kunde", "kunden", "customer", "klient", "client", "clients", "cliente", "clienti"
    ];

    private const string SingleExternRecipe = "add-extern-employee-to-nearest-group";
    private const string BulkExternRecipe = "bulk-add-externs-to-nearest-group";

    [Test]
    public void SpaceSuffixedAnySubstringTerms_MustNotMatchMidWord_InAnySupportedLanguage()
    {
        var corpus = LoadPerLanguageCorpus();
        var violations = new List<string>();
        var unexplained = new List<string>();

        foreach (var (term, recipes, kinds) in LoadSpaceSuffixedSubstringTerms())
        {
            var lowered = term.ToLowerInvariant();
            var offenders = new List<string>();

            foreach (var (language, texts) in corpus)
            {
                foreach (var text in texts)
                {
                    var haystack = text.ToLowerInvariant();
                    var at = haystack.IndexOf(lowered, StringComparison.Ordinal);
                    while (at >= 0)
                    {
                        if (at > 0 && char.IsLetter(haystack[at - 1]))
                        {
                            offenders.Add($"{language}: '...{Snippet(haystack, at, lowered.Length)}...'");
                        }

                        at = haystack.IndexOf(lowered, at + 1, StringComparison.Ordinal);
                    }
                }
            }

            if (offenders.Count == 0)
            {
                continue;
            }

            if (!AcceptedMidWordMatches.TryGetValue(term, out var reason))
            {
                violations.Add(
                    $"'{term}' [{kinds}] ({string.Join(", ", recipes)}) matches mid-word: " +
                    string.Join(" | ", offenders.Distinct(StringComparer.Ordinal).Take(4)));
                continue;
            }

            unexplained.Add($"'{term}': {offenders.Count} hit(s) - accepted: {reason}");
        }

        violations.ShouldBeEmpty(
            "A space-suffixed anySubstring term declares whole-word intent, so it must never match " +
            "mid-word in any of the 25 supported languages. Fix by moving the term to anyWordStart " +
            "(which is \\b-anchored) or by dropping it. If the mid-word hit is genuinely intended, add one " +
            "reviewable AcceptedMidWordMatches entry with the reason. Offenders: " +
            string.Join("; ", violations));

        TestContext.Progress.WriteLine(
            $"space-suffixed terms with accepted mid-word matches: {unexplained.Count}");
        foreach (var line in unexplained)
        {
            TestContext.Progress.WriteLine("  " + line);
        }
    }

    [Test]
    public void SingleAndBulkExternNearestGroup_MustBothVetoTheCustomerDomain()
    {
        var triggers = LoadTriggers();

        triggers.ContainsKey(SingleExternRecipe).ShouldBeTrue(
            $"recipe '{SingleExternRecipe}' not found in {RecipeSeedsFileName}");
        triggers.ContainsKey(BulkExternRecipe).ShouldBeTrue(
            $"recipe '{BulkExternRecipe}' not found in {RecipeSeedsFileName}");

        var single = NoneOfSubstrings(triggers[SingleExternRecipe]);
        var bulk = NoneOfSubstrings(triggers[BulkExternRecipe]);

        var missingInSingle = CustomerDomainVeto.Where(t => !single.Contains(t)).ToList();
        var missingInBulk = CustomerDomainVeto.Where(t => !bulk.Contains(t)).ToList();

        missingInSingle.ShouldBeEmpty(
            $"'{SingleExternRecipe}' must veto the customer domain like its bulk sibling does " +
            "(recipe-authoring.md §2: sibling intents belong in noneOf). Missing: " +
            string.Join(", ", missingInSingle));

        missingInBulk.ShouldBeEmpty(
            $"'{BulkExternRecipe}' lost part of its customer-domain veto. Missing: " +
            string.Join(", ", missingInBulk));
    }

    [Test]
    public void SingleExternNearestGroup_MustNotFireOnACustomerRequest_ButStillFiresOnAnExternEmployee()
    {
        var trigger = LoadTriggers()[SingleExternRecipe];

        // Shaped like the live incident: a customer request that satisfies every allOf condition.
        // Before the customer veto existed, this started the external-employee recipe.
        RecipeTriggerMatcher.Matches(trigger, null,
                "Ordne den Kunden Meier der nächstgelegenen externen Gruppe zu", "de")
            .ShouldBeFalse("a customer request must not start the external-employee recipe");

        RecipeTriggerMatcher.Matches(trigger, null,
                "Ordne den externen Mitarbeiter der nächstgelegenen Gruppe zu", "de")
            .ShouldBeTrue("the legitimate external-employee request must still fire");

        // The goldset phrase that owns this recipe must keep routing to it.
        RecipeTriggerMatcher.Matches(trigger, null,
                "Add an external employee to the group geographically nearest to their address.", "en")
            .ShouldBeTrue("the seeded goal phrase must still fire");
    }

    private static string Snippet(string haystack, int at, int length) =>
        haystack.Substring(Math.Max(0, at - 12), Math.Min(haystack.Length - Math.Max(0, at - 12), length + 20));

    private static HashSet<string> NoneOfSubstrings(RecipeTrigger trigger)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in trigger.NoneOf)
        {
            foreach (var term in condition.AnySubstring ?? [])
            {
                terms.Add(term);
            }

            // A veto term moved into anyWordStartByLocale still vetoes, for its own locale, so the parity
            // check has to see it - otherwise migrating a customer term there would pass this gate
            // silently while the veto is in fact still present but no longer counted.
            if (condition.AnyWordStartByLocale != null)
            {
                foreach (var stems in condition.AnyWordStartByLocale.Values)
                {
                    foreach (var term in stems)
                    {
                        terms.Add(term);
                    }
                }
            }
        }

        return terms;
    }

    // Deliberately reads anySubstring ONLY. anyWordStart and anyWordStartByLocale both compile to
    // \b(?:stem) via MatchesWordStart, so a term in either list cannot match mid-word by construction -
    // feeding them to this gate could never produce a violation and would only suggest coverage that the
    // anchored lists do not need. The locale list is covered where its semantics actually live: the
    // language-scoped matcher tests, and the two vocabulary gates that build phrases from it.
    private static List<(string Term, List<string> Recipes, string Kinds)> LoadSpaceSuffixedSubstringTerms()
    {
        var byTerm = new Dictionary<string, (List<string> Recipes, HashSet<string> Kinds)>(StringComparer.Ordinal);

        foreach (var (name, trigger) in LoadTriggers())
        {
            foreach (var kind in new[] { ("allOf", trigger.AllOf), ("noneOf", trigger.NoneOf) })
            {
                foreach (var condition in kind.Item2)
                {
                    foreach (var term in condition.AnySubstring ?? [])
                    {
                        if (!term.EndsWith(' '))
                        {
                            continue;
                        }

                        if (!byTerm.TryGetValue(term, out var entry))
                        {
                            entry = (new List<string>(), new HashSet<string>(StringComparer.Ordinal));
                        }

                        entry.Recipes.Add(name);
                        entry.Kinds.Add(kind.Item1);
                        byTerm[term] = entry;
                    }
                }
            }
        }

        return byTerm
            .Select(kv => (Term: kv.Key,
                Recipes: kv.Value.Recipes.Distinct(StringComparer.Ordinal).ToList(),
                Kinds: string.Join("/", kv.Value.Kinds.OrderBy(k => k, StringComparer.Ordinal))))
            .OrderBy(t => t.Term, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, RecipeTrigger> LoadTriggers()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllText(Locate(DefinitionsRelativePath, RecipeSeedsFileName)));
        var result = new Dictionary<string, RecipeTrigger>(StringComparer.Ordinal);

        foreach (var element in document.RootElement.GetProperty("recipes").EnumerateArray())
        {
            if (!element.TryGetProperty("trigger", out var triggerElement))
            {
                continue;
            }

            var trigger = JsonSerializer.Deserialize<RecipeTrigger>(triggerElement.GetRawText(), options);
            if (trigger != null)
            {
                result[element.GetProperty("name").GetString() ?? string.Empty] = trigger;
            }
        }

        return result;
    }

    private static Dictionary<string, List<string>> LoadPerLanguageCorpus()
    {
        var corpus = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string language, string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!corpus.TryGetValue(language, out var list))
            {
                list = new List<string>();
                corpus[language] = list;
            }

            list.Add(text);
        }

        using (var document = JsonDocument.Parse(File.ReadAllText(Locate(DefinitionsRelativePath, RecipeSeedsFileName))))
        {
            foreach (var recipe in document.RootElement.GetProperty("recipes").EnumerateArray())
            {
                AddTranslations(recipe, "goalTranslations", Add);
                if (!recipe.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var step in steps.EnumerateArray())
                {
                    AddTranslations(step, "promptTranslations", Add);
                }
            }
        }

        var goldset = TryLocate(GoldsetsRelativePath, TurnGoldsetFileName);
        if (goldset != null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(goldset));
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                Add(item.TryGetProperty("locale", out var locale)
                    ? locale.GetString() ?? "?"
                    : "?", item.TryGetProperty("message", out var message) ? message.GetString() : null);
            }
        }

        var languages = TryLocateDir(PluginsLanguagesRelativePath);
        if (languages != null)
        {
            foreach (var dir in Directory.GetDirectories(languages).OrderBy(d => d, StringComparer.Ordinal))
            {
                var file = Path.Combine(dir, RecipeSynonymsFileName);
                if (!File.Exists(file))
                {
                    continue;
                }

                var code = new DirectoryInfo(dir).Name;
                var map = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file))
                          ?? new Dictionary<string, List<string>>();
                foreach (var phrase in map.Values.SelectMany(v => v ?? new List<string>()))
                {
                    Add(code, phrase);
                }
            }
        }

        return corpus;
    }

    private static void AddTranslations(
        JsonElement element, string propertyName, Action<string, string?> add)
    {
        if (!element.TryGetProperty(propertyName, out var translations)
            || translations.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in translations.EnumerateObject())
        {
            add(prop.Name, prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null);
        }
    }

    private static string Locate(string[] relativePath, string fileName) =>
        TryLocate(relativePath, fileName)
        ?? throw new FileNotFoundException($"Could not locate {string.Join('/', relativePath)}/{fileName}.");

    private static string? TryLocate(string[] relativePath, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            segments.Add(fileName);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? TryLocateDir(string[] relativePath)
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

        return null;
    }
}
