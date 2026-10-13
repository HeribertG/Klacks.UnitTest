// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guard over the shipped vocabulary for the generated collision table. The table is built from the real
/// conversation-signals of the core languages and all language packs, exactly as the loader builds it at startup, and
/// is compared with a checked-in snapshot: a pack change that makes a word a yes in one language and a no in another
/// turns the test red with the actual table, so the collision is a decision and never an accident. The same words are
/// then run through the real detectors to prove that the member languages are settled correctly, that a language which
/// only knows the word inside its own phrases (neutral) lets the rest of the message decide, and that every other
/// language, or none, is left ambiguous. Further guards keep the core vocabulary (which carries no language) free of
/// polarity clashes with the packs, and keep collisions inside the negating lists whose detectors resolve them.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ConversationSignalCollisionTableGuardTests
{
    private const string SnapshotFileName = "conversation-signal-collisions.expected.json";
    private const string SnapshotRelativePath = "Klacks.UnitTest/Domain/Services/Assistant/" + SnapshotFileName;
    private const string UnrelatedLanguage = "und";
    private const int MinimumShippedLanguagePacks = 21;
    private const int CoreLanguageCount = 4;

    private static readonly string[] ResolvedNegatingCategories =
    [
        ConversationSignalCategories.Negations,
        ConversationSignalCategories.Declines
    ];

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<string> _loadErrors = [];
    private IReadOnlyList<ConversationLanguageSignals> _core = [];
    private IReadOnlyList<ConversationLanguageSignals> _packs = [];
    private ConversationSignalCollisionTable _table = ConversationSignalCollisionTable.Empty;

    [OneTimeSetUp]
    public void BuildTableFromTheShippedVocabulary()
    {
        var baseDirectory = RepositoryRootLocator.ApiProject;
        void Collect(string file, Exception ex) => _loadErrors.Add($"{file}: {ex.Message} {ex.InnerException?.Message}");

        _core = ConversationSignalsPluginLoader.ReadCore(baseDirectory, Collect);
        _packs = ConversationSignalsPluginLoader.ReadPacks(baseDirectory, Collect);
        _table = ConversationSignalCollisionTable.Build(_core.Concat(_packs));

        ConversationSignalsPluginLoader.Install(_core, _packs);
    }

    [OneTimeTearDown]
    public void RestoreTheSharedDetectorState()
    {
        ShippedConversationSignals.Uninstall();
    }

    [Test]
    public void EveryLanguagePackAndTheCoreFile_Parses()
    {
        _loadErrors.ShouldBeEmpty(string.Join("\n", _loadErrors));
        _core.Count.ShouldBe(CoreLanguageCount);

        var languagesDirectory = Path.Combine(RepositoryRootLocator.ApiProject, "Plugins", "Languages");
        var packsOnDisk = Directory.GetDirectories(languagesDirectory)
            .Count(directory => File.Exists(Path.Combine(directory, "conversation-signals.json")));

        _packs.Count.ShouldBe(packsOnDisk);
        _packs.Count.ShouldBeGreaterThanOrEqualTo(MinimumShippedLanguagePacks);
    }

    [Test]
    public void TheGeneratedTable_EqualsTheCheckedInSnapshot()
    {
        var expected = ReadSnapshot();
        var actual = ToSnapshot(_table);

        var actualJson = JsonSerializer.Serialize(actual, SnapshotJson);
        var expectedJson = JsonSerializer.Serialize(expected, SnapshotJson);

        actualJson.ShouldBe(
            expectedJson,
            "The language packs now disagree about a word (one lists it as a yes, another as a no) differently than " +
            $"the snapshot says. Decide the collision on purpose - the UI language settles it only for the listed " +
            $"member languages - then replace the content of {SnapshotRelativePath} with this table:\n{actualJson}\n");
    }

    [Test]
    public void EveryCollisionWord_IsSettledByItsMemberLanguages_AndAmbiguousForEveryoneElse()
    {
        var violations = new List<string>();

        foreach (var entry in _table.Entries.Values)
        {
            var word = entry.Word;
            var isSingleToken = !word.Contains(' ');

            foreach (var language in entry.AllLanguages)
            {
                var listsAsYes = entry.AffirmingLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);
                var listsAsNo = entry.NegatingLanguages.Contains(language, StringComparer.OrdinalIgnoreCase);
                var expected = listsAsYes && !listsAsNo
                    ? ConfirmationVerdict.Confirmed
                    : listsAsNo && !listsAsYes ? ConfirmationVerdict.NotConfirmed : ConfirmationVerdict.Ambiguous;

                var assessment = AffirmationDetector.AssessAffirmation(word, language);
                if (assessment.Verdict != expected)
                {
                    violations.Add($"'{word}' is {assessment.Verdict} in {language}, expected {expected}");
                }

                if (isSingleToken && listsAsYes && !listsAsNo && DeclineDetector.IsBareNegation(word, language))
                {
                    violations.Add($"'{word}' is read as a refusal in {language}, which lists it as a yes");
                }
            }

            foreach (var language in entry.NeutralLanguages.Concat(new string?[] { UnrelatedLanguage, null, string.Empty }))
            {
                var assessment = AffirmationDetector.AssessAffirmation(word, language);
                if (assessment.Verdict != ConfirmationVerdict.Ambiguous)
                {
                    violations.Add($"'{word}' is {assessment.Verdict} for language '{language}', expected Ambiguous");
                }

                if (isSingleToken && DeclineDetector.IsBareNegation(word, language))
                {
                    violations.Add($"'{word}' is read as a refusal in '{language}', which cannot settle it");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    [Test]
    public void EveryNeutralLanguage_ReadsTheCollisionWordAsNoSignal_SoTheRestOfTheMessageDecides()
    {
        var violations = new List<string>();

        foreach (var entry in _table.Entries.Values.Where(entry => !entry.Word.Contains(' ')))
        {
            foreach (var language in entry.NeutralLanguages)
            {
                var pack = _packs.Single(candidate => string.Equals(candidate.Language, language, StringComparison.OrdinalIgnoreCase));
                var yes = pack.Affirmations.First(item => !item.Contains(' '));
                var no = pack.Negations.First(item => !item.Contains(' '));

                var confirmed = AffirmationDetector.AssessAffirmation($"{yes} {entry.Word}", language).Verdict;
                var refused = AffirmationDetector.AssessAffirmation($"{no} {entry.Word}", language).Verdict;

                if (confirmed != ConfirmationVerdict.Confirmed)
                {
                    violations.Add($"{language}: '{yes} {entry.Word}' is {confirmed}, expected Confirmed");
                }

                if (refused != ConfirmationVerdict.NotConfirmed || !DeclineDetector.LeadsWithNegation($"{no} {entry.Word}", language))
                {
                    violations.Add($"{language}: '{no} {entry.Word}' is {refused}, expected a refusal");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join("\n", violations));
    }

    [Test]
    public void EveryCollision_SitsInANegatingListWhoseDetectorResolvesIt()
    {
        var ungated = _table.Entries.Values
            .Where(entry => entry.NegatingCategories.Except(ResolvedNegatingCategories).Any())
            .Select(entry => $"'{entry.Word}': {string.Join(", ", entry.NegatingCategories)}")
            .ToList();

        ungated.ShouldBeEmpty(
            "A word that is a yes in one language is a cancellation or correction signal in another. Only the " +
            "negations and declines lists are resolved by the UI language (AffirmationDetector, DeclineDetector); " +
            "RecipeCancellationDetector and ImplicitCorrectionDetector still read their merged list, so the yes would " +
            "end a recipe or count as a correction. Wire the language into that detector before accepting the " +
            "collision: " + string.Join("; ", ungated));
    }

    [Test]
    public void NoCoreAffirmationWord_IsAnyLanguagesNegation_AndNoCoreNegation_IsAnyPacksAffirmation()
    {
        var negatingEntries = _core.Concat(_packs)
            .SelectMany(language => language.Negations
                .Concat(language.Declines).Concat(language.Cancellations).Concat(language.Corrections)
                .Select(entry => (language.Language, Entry: ConversationSignalCollisionTable.Key(entry))))
            .ToList();
        var coreNegations = AffirmationVocabulary.NegationWords
            .Concat(_core.SelectMany(language => language.Negations))
            .Select(ConversationSignalCollisionTable.Key)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<string>();
        violations.AddRange(negatingEntries
            .Where(item => AffirmationVocabulary.AffirmationWords.Contains(item.Entry))
            .Select(item => $"{item.Language} lists the core yes word '{item.Entry}' as a negating signal"));
        violations.AddRange(_packs
            .SelectMany(pack => pack.Affirmations.Select(entry => (pack.Language, Entry: ConversationSignalCollisionTable.Key(entry))))
            .Where(item => coreNegations.Contains(item.Entry))
            .Select(item => $"{item.Language} lists the core refusal '{item.Entry}' as a yes"));

        violations.ShouldBeEmpty(
            "AffirmationVocabulary carries no language, so a core word that collides with a pack cannot be resolved " +
            "by the table. Split the core vocabulary by language and feed it to the table build before accepting " +
            "this: " + string.Join("; ", violations));
    }

    [Test]
    public void NoMultiWordAffirmation_ContainsAWordAnotherLanguageNegates()
    {
        var singleNegations = _core.Concat(_packs)
            .SelectMany(language => language.Negations
                .Concat(language.Declines).Concat(language.Cancellations).Concat(language.Corrections)
                .Where(entry => !entry.Contains(' '))
                .Select(entry => (language.Language, Entry: ConversationSignalCollisionTable.Key(entry))))
            .ToList();

        var violations = _packs
            .SelectMany(pack => pack.Affirmations
                .Select(ConversationSignalCollisionTable.Key)
                .Where(entry => entry.Contains(' '))
                .SelectMany(entry => entry.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(word => singleNegations
                        .Where(negation => negation.Entry == word && negation.Language != pack.Language)
                        .Select(negation => $"{pack.Language} '{entry}' contains '{word}', a negation of {negation.Language}"))))
            .ToList();

        violations.ShouldBeEmpty(
            "The detectors reject a message as soon as ANY negation entry matches, so a yes phrase that contains a " +
            "word another language negates is refused. The collision table compares whole entries only: " +
            string.Join("; ", violations));
    }

    private static SortedDictionary<string, SnapshotEntry> ToSnapshot(ConversationSignalCollisionTable table) =>
        new(table.Entries.ToDictionary(
            pair => pair.Key,
            pair => new SnapshotEntry(
                pair.Value.AffirmingLanguages.ToList(),
                pair.Value.NegatingLanguages.ToList(),
                pair.Value.NegatingCategories.ToList(),
                pair.Value.NeutralLanguages.ToList())),
            StringComparer.Ordinal);

    private static SortedDictionary<string, SnapshotEntry> ReadSnapshot()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Domain", "Services", "Assistant", SnapshotFileName);
        File.Exists(file).ShouldBeTrue($"{SnapshotFileName} must be copied next to the test assembly: {file}");

        var parsed = JsonSerializer.Deserialize<Dictionary<string, SnapshotEntry>>(File.ReadAllText(file), SnapshotJson)!;
        return new SortedDictionary<string, SnapshotEntry>(parsed, StringComparer.Ordinal);
    }

    private sealed record SnapshotEntry(
        List<string> AffirmingLanguages,
        List<string> NegatingLanguages,
        List<string> NegatingCategories,
        List<string> NeutralLanguages);
}
