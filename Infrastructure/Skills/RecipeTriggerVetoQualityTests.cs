// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the recipe-trigger veto vocabulary (W3.6): every recipe must carry the full core-language
/// question-word veto in its noneOf startsWith block, so an information question ("How do I create an
/// employee?") can never deterministically start the mutation recipe. The DE/fr/it part was fixed
/// 2026-08-28; the EN part and the fr/it copula veto were added 2026-08-30; the English copula
/// ("is "/"are ") was completed 2026-09-02 after a review found it in only 21 of 25 recipes (see
/// docs/knowledge/klacksy-recipe-trigger-noneof-question-word-gap-2026-08-28.md).
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeTriggerVetoQualityTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeSynonymsFileName = "recipe-synonyms.json";
    private const string RecipeVetoesFileName = "recipe-vetoes.json";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly string[] PluginsLanguagesRelativePath =
    [
        "Klacks.Api", "Plugins", "Languages"
    ];

    private static readonly string[] RequiredEnglishQuestionVeto =
    [
        "how ", "what ", "why ", "when ", "where ", "who ", "whom ", "which ", "show ", "explain", "is there",
        "is ", "are "
    ];

    private static readonly string[] RequiredCopulaVeto =
    [
        "est-ce", "c'est", "cos'è", "è "
    ];

    // setup-consultation is an informational/advisory recipe (search+ask only, no mutate step)
    // whose own trigger phrases open with the vetoed words themselves ("Wie fange ich an?",
    // "How do I get started?"). The generic veto would self-veto it. Owner-approved exemption.
    // Exemption is deliberately a named allowlist, not an automatic "no mutate step" rule, so a
    // future recipe never loses this guard just because someone removes its last mutate step -
    // every exemption stays one reviewable line here. Covers only the question-word veto tests
    // below; the create-verb guard (anyWordStart: erstell/anleg/erfass/create/crée/crea) that
    // routes "Erstelle einen Dienst, wie fange ich an?" to create-shift-order is untouched and
    // must not be exempted by any test that checks it.
    private static readonly string[] VetoExemptRecipes = ["setup-consultation", "plan-delivery-deadline", "period-close-schedule"];

    /// <summary>
    /// The W1b obligation: noneOf carries question words for de/en/fr/it only, so an information
    /// question in one of the 21 plugin languages has no veto surface at all and can deterministically
    /// start a mutation recipe. This table is the obligation, stated independently of the pack files
    /// that satisfy it - the same split the core-language tests above use against recipe-seeds.json.
    /// Scope is a CLOSED grammatical class (interrogatives, question particles, unambiguous copula
    /// questions), never domain vocabulary: machine-generated domain vetoes in 21 languages without a
    /// native-speaker review are the bug class that produced the 'alle '/'nos ' round.
    /// Entries were chosen to be interrogative at message start WITHOUT exception. Deliberately absent:
    /// a bare copula or existential ("hay ", "há ", "je ", "υπάρχει ", "יש ", "ada ", "există "),
    /// because those also open statements - "Hay que añadir", "Je potřeba přidat" - and a false veto
    /// silently suppresses a real mutation request. ar "من " (who) is absent for the same reason:
    /// "من فضلك" opens a polite imperative.
    /// Two entries were removed on owner review for the same failure mode. pt "como " is both "how" and
    /// "as/like", so "Como combinado, adicione o funcionário" would have been vetoed - Italian "come "
    /// carries the identical ambiguity in the core vocabulary, but precedent is not a justification.
    /// sv "var " is the imperative "var god" (please), the past tense of "vara", and "var och en" (each
    /// one); it is replaced by the qualified "var finns ", "var är " and "vart ".
    /// </summary>
    private static readonly Dictionary<string, string[]> RequiredPluginQuestionVeto = new(StringComparer.Ordinal)
    {
        ["ar"] = ["هل ", "كيف ", "ماذا ", "لماذا ", "متى ", "أين ", "أي "],
        ["cs"] = ["jak ", "co ", "proč ", "kdy ", "kde ", "kdo ", "který ", "která ", "které "],
        ["da"] =
        [
            "hvordan ", "hvad ", "hvorfor ", "hvornår ", "hvor ", "hvem ", "hvilken ", "hvilket ", "hvilke ",
            "er der "
        ],
        ["el"] = ["πώς ", "τι ", "γιατί ", "πότε ", "πού ", "ποιος ", "ποια ", "ποιο "],
        ["es"] = ["cómo ", "qué ", "por qué ", "cuándo ", "dónde ", "quién ", "quiénes ", "cuál ", "cuáles "],
        ["fi"] = ["miten ", "kuinka ", "mitä ", "miksi ", "milloin ", "missä ", "kuka ", "onko ", "voiko "],
        ["he"] = ["האם ", "איך ", "כיצד ", "מה ", "למה ", "מדוע ", "מתי ", "איפה ", "היכן ", "מי ", "איזה "],
        ["id"] =
        [
            "apakah ", "bagaimana ", "gimana ", "apa ", "kenapa ", "mengapa ", "kapan ", "siapa ", "di mana ",
            "dimana "
        ],
        ["ja"] =
        [
            "なぜ", "どうして", "どのように", "どうやって", "どんな", "何を", "何が", "何の", "いつの", "どこに",
            "どこで", "誰が", "誰を", "どちら"
        ],
        ["ko"] = ["왜 ", "어떻게 ", "언제 ", "어디 ", "누구 ", "무엇 ", "뭐 ", "어느 ", "무슨 "],
        ["ms"] =
        [
            "adakah ", "bagaimana ", "macam mana ", "apa ", "kenapa ", "mengapa ", "bilakah ", "bila ",
            "di mana ", "siapa ", "yang mana "
        ],
        ["nb"] =
        [
            "hvordan ", "hva ", "hvorfor ", "når ", "hvor ", "hvem ", "hvilken ", "hvilket ", "hvilke ",
            "finnes det ", "er det "
        ],
        ["nl"] = ["hoe ", "wat ", "waarom ", "wanneer ", "waar ", "wie ", "welke ", "welk ", "is er ", "zijn er "],
        ["pl"] = ["czy ", "jak ", "co ", "dlaczego ", "kiedy ", "gdzie ", "kto ", "który ", "która ", "które "],
        ["pt"] =
        [
            "o que ", "por que ", "porque ", "quando ", "onde ", "quem ", "qual ", "quais "
        ],
        ["ro"] = ["cum ", "ce ", "de ce ", "când ", "unde ", "cine ", "care "],
        ["sv"] =
        [
            "hur ", "vad ", "varför ", "när ", "var finns ", "var är ", "vart ", "vem ", "vilken ",
            "vilket ", "vilka ", "finns det ", "är det "
        ],
        ["th"] = ["ทำไม", "อย่างไร", "ยังไง", "อะไร", "เมื่อไหร่", "ที่ไหน", "ใคร", "ไหน"],
        ["vi"] =
        [
            "làm sao ", "làm thế nào ", "tại sao ", "vì sao ", "cái gì ", "khi nào ", "bao giờ ", "ở đâu ",
            "ai ", "có phải "
        ],
        ["zh-CN"] =
        [
            "为什么", "怎么", "怎样", "如何", "什么", "哪", "谁", "多少", "何时", "哪里", "是否", "有没有"
        ],
        ["zh-TW"] =
        [
            "為什麼", "怎麼", "怎樣", "如何", "什麼", "哪", "誰", "多少", "何時", "哪裡", "是否", "有沒有"
        ]
    };

    /// <summary>
    /// Scripts written without word separators, judged by the codebase's own authority:
    /// PluginPhraseMatcher.UsesNonSegmentedScript covers Thai (0E00-0E7F), Kana (3040-30FF), Han
    /// (3400-9FFF) and Han Compatibility (F900-FAFF). A trailing space is the author's "whole word,
    /// not a stem" marker and is compiled to ^term\b - but \b asserts a boundary between \w and
    /// non-\w, and every character of these scripts IS a letter, so 何\b fails against 何か and the
    /// term never matches. The veto would be silently inert. These packs must write bare prefixes
    /// instead, and their interrogatives above are therefore particle-qualified (何を, どこに) where a
    /// bare stem would over-match an indefinite pronoun (何か "something", いつも "always").
    /// Korean is deliberately absent even though the W1b hand-off counts it among the five: Hangul
    /// (AC00-D7AF) lies outside every range above and Korean does separate words with spaces, so \b
    /// works there and is strictly better - ^누구\b covers "누구" without matching "누구나" (anyone),
    /// which the bare prefix a non-segmented classification would force cannot do.
    /// </summary>
    private static readonly string[] NonSegmentedScriptLanguages = ["ja", "th", "zh-CN", "zh-TW"];

    [Test]
    public void EveryRecipe_MustVetoEnglishQuestionWords()
    {
        var violations = new List<string>();

        foreach (var (name, trigger) in LoadTriggers())
        {
            if (VetoExemptRecipes.Contains(name))
            {
                continue;
            }

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
            if (VetoExemptRecipes.Contains(name))
            {
                continue;
            }

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

    /// <summary>
    /// Sourced from the obligation table, NOT from Directory.GetDirectories over the packs. Enumerating
    /// the filesystem would return an empty set while no recipe-vetoes.json exists anywhere, and the
    /// test would pass vacantly - green before the feature, green if every pack file were deleted.
    /// </summary>
    public static IEnumerable<string> PluginLanguagesRequiringQuestionVeto() =>
        RequiredPluginQuestionVeto.Keys.OrderBy(code => code, StringComparer.Ordinal);

    [TestCaseSource(nameof(PluginLanguagesRequiringQuestionVeto))]
    public void EveryPack_MustVetoItsOwnLanguageQuestionWords(string language)
    {
        var required = RequiredPluginQuestionVeto[language];
        var violations = new List<string>();

        // The pack's own recipe-synonyms.json is the authoritative list of recipes this language speaks
        // for: a recipe that routes on plugin synonyms but carries no plugin veto is exactly the blind
        // spot W1b closes. Driving off the veto file instead would let a pack omit recipes silently.
        var synonymSlugs = LoadPackSlugs(language, RecipeSynonymsFileName);
        if (!TryLoadPack(language, RecipeVetoesFileName, out var vetoes))
        {
            violations.Add($"pack '{language}' has no {RecipeVetoesFileName} at all");
        }
        else
        {
            foreach (var slug in synonymSlugs)
            {
                if (VetoExemptRecipes.Contains(slug))
                {
                    continue;
                }

                if (!vetoes.TryGetValue(slug, out var terms))
                {
                    violations.Add($"{language}/{slug}: no veto entry");
                    continue;
                }

                var have = new HashSet<string>(terms, StringComparer.OrdinalIgnoreCase);
                var missing = required.Where(term => !have.Contains(term)).ToList();
                if (missing.Count > 0)
                {
                    violations.Add($"{language}/{slug}: missing question veto {string.Join(", ", missing)}");
                }
            }

            // A veto for a recipe the pack contributes no synonyms to is dead weight, and after a rename
            // it is the only surviving trace of the old slug - the silent half of a broken migration.
            foreach (var stale in vetoes.Keys.Except(synonymSlugs, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            {
                violations.Add($"{language}/{stale}: veto entry names a recipe the synonym pack does not");
            }
        }

        violations.ShouldBeEmpty(
            $"The plugin language '{language}' routes recipes through {RecipeSynonymsFileName} but its " +
            $"{RecipeVetoesFileName} does not veto that language's question words, so an information " +
            "question in it can still deterministically start a mutation recipe. Offenders: " +
            string.Join("; ", violations));
    }

    [TestCaseSource(nameof(NonSegmentedScriptPackLanguages))]
    public void NonSegmentedScriptVetoes_MustNotCarryATrailingSpace(string language)
    {
        if (!TryLoadPack(language, RecipeVetoesFileName, out var vetoes))
        {
            // Covered by EveryPack_MustVetoItsOwnLanguageQuestionWords; failing here too would report
            // one missing file as two unrelated defects.
            Assert.Pass($"{language} has no {RecipeVetoesFileName} yet; the obligation test reports it.");
        }

        var offenders = new List<string>();
        foreach (var (slug, terms) in vetoes)
        {
            foreach (var term in terms)
            {
                if (term.Length > 0 && char.IsWhiteSpace(term[^1]))
                {
                    offenders.Add($"{slug}: '{term}'");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"In {language} a trailing space marks the term as a whole word and compiles it to ^term\\b. " +
            "\\b needs a \\w / non-\\w transition, and every character of this script is a letter, so the " +
            "pattern can never match and the veto is silently inert. Write bare prefixes instead. " +
            "Offenders: " + string.Join("; ", offenders));
    }

    public static IEnumerable<string> NonSegmentedScriptPackLanguages() =>
        NonSegmentedScriptLanguages.OrderBy(code => code, StringComparer.Ordinal);

    /// <summary>
    /// A pack synonym that opens with one of its own language's veto terms is vetoed by the very
    /// vocabulary installed to protect it, which kills that recipe's synonym route silently. This is why
    /// setup-consultation is left out of the pack files outright rather than only exempted above: its
    /// phrases open with question words in 15 of the 21 languages ("cómo empiezo a usar el sistema",
    /// "hvordan kommer jeg egentlig i gang"), so an installed veto would have dead-ended all of them.
    /// The comparison reproduces MatchesStartsWith semantics rather than a naive prefix test: a
    /// space-terminated term is a whole word, so "hvor " must not be reported against "hvordan".
    /// </summary>
    [TestCaseSource(nameof(PluginLanguagesRequiringQuestionVeto))]
    public void NoPackSynonym_MayOpenWithItsOwnLanguageVetoTerm(string language)
    {
        if (!TryLoadPack(language, RecipeVetoesFileName, out var vetoes))
        {
            Assert.Pass($"no {RecipeVetoesFileName} for '{language}'; the obligation test reports that.");
        }

        if (!TryLoadPack(language, RecipeSynonymsFileName, out var synonyms))
        {
            Assert.Fail($"pack '{language}' has no {RecipeSynonymsFileName} to check the veto terms against.");
        }

        var terms = vetoes.Values
            .SelectMany(list => list)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        terms.Count.ShouldBeGreaterThan(0,
            $"pack '{language}' installs no veto terms, so this test would pass without proving anything");

        // Per recipe and only for recipes that actually carry a veto: an exempt one such as
        // setup-consultation has no entry in the pack at all, so at runtime nothing vetoes its synonyms
        // and reporting them here would be a false alarm. Should it ever be given an entry, this loop
        // starts covering it and its self-vetoing phrases fail - which is the protection we want.
        var offenders = new List<string>();
        foreach (var (slug, slugTerms) in vetoes)
        {
            if (!synonyms.TryGetValue(slug, out var phrases))
            {
                continue;
            }

            foreach (var phrase in phrases)
            {
                var trimmedPhrase = phrase.Trim();
                foreach (var term in slugTerms.Where(term => OpensWith(trimmedPhrase, term)))
                {
                    offenders.Add($"{slug}: '{phrase}' opens with its own veto term '{term}'");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"These {RecipeSynonymsFileName} phrases in pack '{language}' would be vetoed by that pack's " +
            $"own {RecipeVetoesFileName}, so the synonym route could never fire for them. Offenders: " +
            string.Join("; ", offenders));
    }

    private static bool OpensWith(string phrase, string term)
    {
        var word = term.TrimEnd();
        if (word.Length == 0)
        {
            return false;
        }

        // A term written with a trailing space is a whole word: it only vetoes when what follows is a
        // boundary. Terms without one are open stems and match as a plain prefix, which is also the only
        // thing that can work in a script with no word separators.
        return word.Length < term.Length
            ? phrase.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase)
              || string.Equals(phrase, word, StringComparison.OrdinalIgnoreCase)
            : phrase.StartsWith(word, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> LoadPackSlugs(string language, string fileName)
    {
        if (!TryLoadPack(language, fileName, out var pack))
        {
            throw new FileNotFoundException(
                $"Language pack '{language}' has no {fileName}; the recipe veto gate needs it as the " +
                "authoritative recipe list for that language.");
        }

        return pack.Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static bool TryLoadPack(string language, string fileName, out Dictionary<string, List<string>> pack)
    {
        pack = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var file = Path.Combine(LocatePluginsLanguagesDir(), language, fileName);
        if (!File.Exists(file))
        {
            return false;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
            File.ReadAllText(file), jsonOptions);
        if (deserialized == null)
        {
            return false;
        }

        foreach (var (slug, terms) in deserialized)
        {
            pack[slug] = terms;
        }

        return true;
    }

    private static string LocatePluginsLanguagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(PluginsLanguagesRelativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {string.Join('/', PluginsLanguagesRelativePath)} by walking up from the test base directory.");
    }
}
