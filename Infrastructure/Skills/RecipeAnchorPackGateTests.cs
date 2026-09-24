// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pack gate for recipe-anchors.json, the per-language subject vocabulary of the semantic recipe anchor.
/// Every pack file must parse, name exactly the seeded recipes, carry only non-empty lower-case terms of a
/// minimum length and never reuse a word of the pack's own question/mutation deny list (a verb or question
/// word as anchor would let exactly the messages through the anchor exists to stop). The deny rule is the
/// one the list pipeline used (tools/gates.py of the 2026-09-24 hand-off): a deny entry written with a
/// trailing space is a whole word and collides on equality, every other entry collides as a prefix in
/// either direction once both sides have three characters. On top, the real files are run through the
/// matcher: deferred-notes and verb-leak sentences must anchor no multi-condition recipe with all 21 packs
/// loaded, and a Spanish message under a German UI must reach the recipe it names.
/// The language list is explicit, not read from the directory, so a deleted pack fails instead of passing
/// vacantly. The non-core sentences are unverified machine translations from the hand-off simulation.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class RecipeAnchorPackGateTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string RecipeAnchorsFileName = "recipe-anchors.json";
    private const string RecipeVetoesFileName = "recipe-vetoes.json";
    private const string MutationIntentFileName = "mutation-intent.json";
    private const string InfoQuestionLeadsKey = "infoQuestionLeads";
    private const string MutationPhrasesKey = "mutationPhrases";
    private const string WholeWordMarker = " ";
    private const int MinPrefixCollisionLength = 3;
    private const int MinLatinTermLength = 4;
    private const int MinCjkTermLength = 2;
    private const int MinGluedScriptTermLength = 3;
    private const string German = "de";
    private const string Spanish = "es";
    private const string ExternsRecipe = "bulk-add-externs-to-nearest-group";

    private static readonly string[] PackLanguages =
    [
        "ar", "cs", "da", "el", "es", "fi", "he", "id", "ja", "ko", "ms",
        "nb", "nl", "pl", "pt", "ro", "sv", "th", "vi", "zh-CN", "zh-TW"
    ];

    private static readonly HashSet<string> CjkLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "ja", "ko", "zh-CN", "zh-TW" };

    private static readonly HashSet<string> GluedScriptLanguages =
        new(StringComparer.OrdinalIgnoreCase) { "th", "ar", "he" };

    private static readonly string[] DefinitionsRelativePath = ["Klacks.Api", "Application", "Skills", "Definitions"];

    private static readonly string[] PluginsLanguagesRelativePath = ["Klacks.Api", "Plugins", "Languages"];

    private static readonly JsonSerializerOptions JsonReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly (string Language, string Message)[] LeakSentences =
    [
        ("de", "Lies bitte meine zurückgestellten Notizen vor."),
        ("de", "Hast du noch offene Hinweise für mich?"),
        ("de", "Zurückgestellte Notizen verwalten"),
        ("de", "Verschiebe meine Notizen auf morgen"),
        ("de", "Erfasse eine Notiz für morgen"),
        ("de", "Fasse meine Notizen zusammen"),
        ("es", "Léeme por favor mis notas pospuestas."),
        ("es", "¿Tienes todavía avisos pendientes para mí?"),
        ("es", "Gestionar las notas pospuestas"),
        ("es", "Agrupa mis notas por fecha"),
        ("es", "Mueve mis notas pospuestas a mañana"),
        ("es", "Registra una nota para mañana"),
        ("pt", "Lê por favor as minhas notas adiadas."),
        ("pt", "Ainda tens avisos pendentes para mim?"),
        ("pt", "Gerir as notas adiadas"),
        ("pl", "Przeczytaj proszę moje odłożone notatki."),
        ("pl", "Czy masz jeszcze dla mnie jakieś otwarte wskazówki?"),
        ("pl", "Zarządzaj odłożonymi notatkami"),
        ("pl", "Przenieś moje notatki na jutro"),
        ("pl", "Zapisz notatkę na jutro"),
        ("pl", "Połącz moje notatki w jedną"),
        ("ja", "保留中のメモを読んでください。"),
        ("ja", "まだ未処理のヒントはありますか？"),
        ("ja", "保留中のメモを管理する"),
        ("ja", "メモを明日に移動して"),
        ("ja", "メモを登録して"),
        ("ja", "メモを一つにまとめて"),
        ("zh-CN", "请读一下我暂存的笔记。"),
        ("zh-CN", "你还有给我的未处理提示吗？"),
        ("zh-CN", "管理暂存的笔记"),
        ("th", "ช่วยอ่านบันทึกที่เลื่อนไว้ของฉันหน่อย"),
        ("th", "ยังมีคำแนะนำที่ค้างอยู่สำหรับฉันไหม"),
        ("th", "จัดการบันทึกที่เลื่อนไว้")
    ];

    private static List<RecipeSeedDefinition> _recipes = null!;
    private static Dictionary<string, Dictionary<string, List<string>>> _anchorsByLanguage = null!;

    [OneTimeSetUp]
    public void LoadSeedsAndPacks()
    {
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(
            File.ReadAllText(LocateFile(DefinitionsRelativePath, RecipeSeedsFileName)), JsonReadOptions);
        _recipes = seed!.Recipes.OrderBy(r => r.SortOrder).ToList();

        _anchorsByLanguage = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var language in PackLanguages)
        {
            var path = LocatePackFile(language, RecipeAnchorsFileName);
            if (path != null)
            {
                _anchorsByLanguage[language] = ReadVocabulary(path);
            }
        }
    }

    public static IEnumerable<string> Languages() => PackLanguages;

    [TestCaseSource(nameof(Languages))]
    public void PackFile_Exists_AndNamesExactlyTheSeededRecipes(string language)
    {
        _anchorsByLanguage.ShouldContainKey(language, $"{language}/{RecipeAnchorsFileName} is missing or unreadable");

        var expected = _recipes.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actual = _anchorsByLanguage[language].Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

        actual.ShouldBe(expected);
    }

    [TestCaseSource(nameof(Languages))]
    public void EveryRecipe_CarriesWellFormedTerms(string language)
    {
        var violations = new List<string>();
        var minLength = MinTermLength(language);

        foreach (var (recipe, terms) in _anchorsByLanguage[language])
        {
            if (terms is not { Count: > 0 })
            {
                violations.Add($"{recipe}: no terms");
                continue;
            }

            foreach (var term in terms)
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    violations.Add($"{recipe}: blank term");
                }
                else if (term != term.Trim())
                {
                    violations.Add($"{recipe}: '{term}' has leading or trailing whitespace");
                }
                else if (term != term.ToLowerInvariant())
                {
                    violations.Add($"{recipe}: '{term}' is not lower case");
                }
                else if (term.Replace(WholeWordMarker, string.Empty).Length < minLength)
                {
                    violations.Add($"{recipe}: '{term}' is shorter than {minLength}");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [TestCaseSource(nameof(Languages))]
    public void NoTerm_CollidesWithThePackDenyList(string language)
    {
        var deny = LoadDenyList(language);
        deny.Count.ShouldBeGreaterThan(0, $"{language} has no deny list; the gate would pass vacantly");

        var violations = _anchorsByLanguage[language]
            .SelectMany(entry => entry.Value.Select(term => (Recipe: entry.Key, Term: term)))
            .Where(item => deny.Any(entry => Collides(item.Term, entry)))
            .Select(item => $"{item.Recipe}: '{item.Term}'")
            .Distinct()
            .ToList();

        violations.ShouldBeEmpty();
    }

    public static IEnumerable<TestCaseData> LeakCases() =>
        LeakSentences.SelectMany(sentence => new[] { sentence.Language, German }
            .Distinct(StringComparer.Ordinal)
            .Select(ui => new TestCaseData(sentence.Message, ui).SetArgDisplayNames($"{sentence.Language}:{sentence.Message}", ui)));

    [TestCaseSource(nameof(LeakCases))]
    public void DeferredNotesAndVerbLeakSentences_AnchorNoMultiConditionRecipe_WithAllPacksLoaded(
        string message, string uiLanguage)
    {
        _anchorsByLanguage.Count.ShouldBe(PackLanguages.Length);

        var anchored = _recipes
            .Where(r => r.Trigger.AllOf.Count > 1)
            .Where(r => RecipeTriggerMatcher.HasSemanticAnchor(
                r.Trigger, message, language: uiLanguage, packAnchors: PackAnchorsFor(r.Name)))
            .Select(r => r.Name)
            .ToList();

        anchored.ShouldBeEmpty();
    }

    [TestCase("asigna a los subcontratados al grupo más cercano")]
    [TestCase("pon a los autónomos en el equipo de su región")]
    public void SpanishMessageUnderGermanUi_ReachesTheExternsRecipe_OnlyThroughTheSpanishPack(string message)
    {
        var trigger = _recipes.Single(r => r.Name == ExternsRecipe).Trigger;
        var spanishOnly = new Dictionary<string, List<string>>
        {
            [Spanish] = _anchorsByLanguage[Spanish][ExternsRecipe]
        };

        RecipeTriggerMatcher.CountAnchors(trigger, message, language: German).ShouldBe(0);
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, message, language: German).ShouldBeFalse();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, message, language: German, packAnchors: spanishOnly)
            .ShouldBeTrue();
        RecipeTriggerMatcher.HasSemanticAnchor(trigger, message, language: German, packAnchors: PackAnchorsFor(ExternsRecipe))
            .ShouldBeTrue();
    }

    [Test]
    public void SingleConditionRecipes_StayUngated_WithAllPacksLoaded()
    {
        const string message = "Lies bitte meine zurückgestellten Notizen vor.";

        var gated = _recipes
            .Where(r => r.Trigger.AllOf.Count == 1)
            .Where(r => !RecipeTriggerMatcher.HasSemanticAnchor(
                r.Trigger, message, language: German, packAnchors: PackAnchorsFor(r.Name)))
            .Select(r => r.Name)
            .ToList();

        gated.ShouldBeEmpty();
    }

    private static Dictionary<string, List<string>> PackAnchorsFor(string recipeName) =>
        _anchorsByLanguage
            .Where(pack => pack.Value.ContainsKey(recipeName))
            .ToDictionary(pack => pack.Key, pack => pack.Value[recipeName], StringComparer.Ordinal);

    private static int MinTermLength(string language)
    {
        if (CjkLanguages.Contains(language))
        {
            return MinCjkTermLength;
        }

        return GluedScriptLanguages.Contains(language) ? MinGluedScriptTermLength : MinLatinTermLength;
    }

    private static bool Collides(string term, string denyEntry)
    {
        if (denyEntry.EndsWith(WholeWordMarker, StringComparison.Ordinal))
        {
            return string.Equals(term, denyEntry.Trim(), StringComparison.Ordinal);
        }

        return denyEntry.Length >= MinPrefixCollisionLength
            && term.Length >= MinPrefixCollisionLength
            && (term.StartsWith(denyEntry, StringComparison.Ordinal) || denyEntry.StartsWith(term, StringComparison.Ordinal));
    }

    private static HashSet<string> LoadDenyList(string language)
    {
        var deny = new HashSet<string>(StringComparer.Ordinal);

        var vetoesPath = LocatePackFile(language, RecipeVetoesFileName);
        if (vetoesPath != null)
        {
            foreach (var term in ReadVocabulary(vetoesPath).Values.SelectMany(terms => terms))
            {
                deny.Add(term.ToLowerInvariant());
            }
        }

        var mutationPath = LocatePackFile(language, MutationIntentFileName);
        if (mutationPath != null)
        {
            var mutation = ReadVocabulary(mutationPath);
            foreach (var lead in mutation.GetValueOrDefault(InfoQuestionLeadsKey) ?? [])
            {
                deny.Add(lead.Trim().ToLowerInvariant() + WholeWordMarker);
            }

            foreach (var phrase in mutation.GetValueOrDefault(MutationPhrasesKey) ?? [])
            {
                deny.Add(phrase.Trim().ToLowerInvariant());
            }
        }

        return deny;
    }

    private static Dictionary<string, List<string>> ReadVocabulary(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), JsonReadOptions)
        ?? new Dictionary<string, List<string>>();

    private static string? LocatePackFile(string language, string fileName)
    {
        var directory = LocateFile(PluginsLanguagesRelativePath, string.Empty);
        var path = Path.Combine(directory, language, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string LocateFile(string[] relativePath, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(relativePath);
            var candidate = Path.Combine(segments.ToArray());
            if (fileName.Length == 0 ? Directory.Exists(candidate) : File.Exists(Path.Combine(candidate, fileName)))
            {
                return fileName.Length == 0 ? candidate : Path.Combine(candidate, fileName);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', relativePath)}/{fileName} from {AppContext.BaseDirectory}");
    }
}
