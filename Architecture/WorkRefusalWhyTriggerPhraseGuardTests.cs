// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the question of the "Why? Ask Klacksy" link in the toast of a refused single work booking (T5 part b,
/// schedule.workConflict.whyTriggerPhrase). The click sends it as the user's own chat message, and the chat request has
/// no skill parameter: diagnose_work_booking reaches the toolset through the keyword guarantee. So for each of the 25
/// language packs the phrase - filled with a neutral sample, a realistic one in that language and the realistic one with
/// the employee number (schedule.workConflict.clientWithNumber) - is run through
/// the real SkillMatchingEngine against the whole shipped seed catalogue, with the pack's skill-synonyms.json merged in
/// as the plugin installer does, and the diagnosis must be inside the guaranteed cap. The engine ranks by the number of
/// matched terms, and generic words of the phrase ("persona", "turno") also feed other skills, so a pack phrase may need
/// two diagnosis synonyms (es). In de/en/fr/it the guarantee is BookingDiagnosisIntentResolver on the schedule page
/// ("why ... not" plus the date), which is cap-protected; there the keyword match is not required. The resolver also has
/// to fire off the schedule page (route null) through its date signal alone, with the date written the way the UI
/// formats it in each core locale (de 10.03.2027, en 3/10/2027, fr and it 10/03/2027). In no language may
/// the phrase read as a yes or a no for the real detectors, so it cannot redeem or refuse a held confirmation. Not
/// covered: user data that happens to contain many keywords of other skills, the later toolset truncation and the
/// model's choice among the guaranteed skills - only a goldset run measures that. Inconclusive when Klacks.Ui is not
/// checked out next to Klacks.Api.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class WorkRefusalWhyTriggerPhraseGuardTests
{
    private const string TriggerPhraseKey = "schedule.workConflict.whyTriggerPhrase";
    private const string ClientWithNumberKey = "schedule.workConflict.clientWithNumber";
    private const string NamePlaceholder = "{{name}}";
    private const string NumberPlaceholder = "{{number}}";
    private const string SampleEmployeeNumber = "1234";
    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const string SkillSynonymsFileName = "skill-synonyms.json";
    private const string JsonExtension = ".json";
    private const string ClientPlaceholder = "{{client}}";
    private const string DatePlaceholder = "{{date}}";
    private const string ShiftPlaceholder = "{{shift}}";
    private const int ExpectedLanguages = 25;

    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private const string NeutralClient = "Xq Zv";
    private const string RealisticClient = "Anna Muster";
    private const string SampleDate = "10.03.2027";
    private const string NeutralShift = "Qzx";

    private static readonly Dictionary<string, string> CoreLocaleDates = new(StringComparer.Ordinal)
    {
        ["de"] = "10.03.2027", ["en"] = "3/10/2027", ["fr"] = "10/03/2027", ["it"] = "10/03/2027",
    };

    private static readonly Dictionary<string, string> LocalShiftNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "Frühdienst", ["en"] = "Early shift", ["fr"] = "Service du matin", ["it"] = "Turno del mattino",
        ["ar"] = "الوردية الصباحية", ["cs"] = "Ranní směna", ["da"] = "Morgenvagt", ["el"] = "Πρωινή βάρδια",
        ["es"] = "Turno de mañana", ["fi"] = "Aamuvuoro", ["he"] = "משמרת בוקר", ["id"] = "Shift pagi",
        ["ja"] = "早番", ["ko"] = "오전 근무", ["ms"] = "Syif pagi", ["nb"] = "Morgenvakt",
        ["nl"] = "Vroege dienst", ["pl"] = "Zmiana poranna", ["pt"] = "Turno da manhã", ["ro"] = "Tura de dimineață",
        ["sv"] = "Morgonpass", ["th"] = "กะเช้า", ["vi"] = "Ca sáng", ["zh-CN"] = "早班", ["zh-TW"] = "早班",
    };

    [Test]
    public void TheQuestion_GuaranteesTheBookingDiagnosisByKeyword_InEveryLanguagePack()
    {
        var phrases = PhrasesOrInconclusive();
        var problems = new List<string>();

        foreach (var (language, template, personLabel, packSynonyms) in phrases.Where(entry => entry.PackSynonyms != null))
        {
            var catalogue = SeedSkillCatalog.Load();
            MergePackSynonyms(catalogue, language, packSynonyms!);

            foreach (var subject in SamplesFor(language, personLabel))
            {
                var phrase = Fill(template, subject);
                var guaranteed = SkillMatchingEngine.TopKeywordMatchedSkillNames(catalogue, phrase);
                if (!guaranteed.Contains(SkillNames.DiagnoseWorkBooking))
                {
                    problems.Add($"{language}: '{phrase}' guarantees [{string.Join(", ", guaranteed)}]");
                }
            }
        }

        phrases.Count.ShouldBe(ExpectedLanguages);
        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheQuestion_FiresTheWhyNotRule_OnTheSchedulePage_InTheCoreLanguages()
    {
        var problems = new List<string>();
        var core = PhrasesOrInconclusive().Where(entry => CoreLanguages.Contains(entry.Language)).ToList();
        core.Count.ShouldBe(CoreLanguages.Length);
        foreach (var (language, template, personLabel, _) in core)
        {
            foreach (var subject in SamplesFor(language, personLabel))
            {
                var phrase = Fill(template, subject);
                if (!BookingDiagnosisIntentResolver.GuaranteedSkillNames(phrase, ProactiveActionRoutes.Schedule)
                        .Contains(SkillNames.DiagnoseWorkBooking))
                {
                    problems.Add($"{language}: '{phrase}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheQuestion_FiresTheWhyNotRule_OffTheSchedulePage_ThroughTheLocaleDate_InTheCoreLanguages()
    {
        var problems = new List<string>();
        var core = PhrasesOrInconclusive().Where(entry => CoreLanguages.Contains(entry.Language)).ToList();
        core.Count.ShouldBe(CoreLanguages.Length);
        foreach (var (language, template, personLabel, _) in core)
        {
            foreach (var (client, _, shift) in SamplesFor(language, personLabel))
            {
                var phrase = Fill(template, (client, CoreLocaleDates[language], shift));
                if (!ScheduleReadIntentResolver.HasDateSignal(phrase)
                    || !BookingDiagnosisIntentResolver.GuaranteedSkillNames(phrase, null).Contains(SkillNames.DiagnoseWorkBooking))
                {
                    problems.Add($"{language}: '{phrase}'");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void TheQuestion_IsNeitherAYesNorANo_ForTheRealDetectors()
    {
        var phrases = PhrasesOrInconclusive();
        ShippedConversationSignals.Install();
        try
        {
            var problems = new List<string>();
            foreach (var (language, template, personLabel, _) in phrases)
            {
                foreach (var readAs in SamplesFor(language, personLabel)
                    .Select(subject => Fill(template, subject))
                    .SelectMany(phrase => new[] { (Phrase: phrase, Language: language), (Phrase: phrase, Language: (string?)null) }))
                {
                    var phrase = readAs.Phrase;
                    if (AffirmationDetector.AssessExplicitConfirmation(phrase, readAs.Language).Verdict != ConfirmationVerdict.NotConfirmed
                        || AffirmationDetector.AssessAffirmation(phrase, readAs.Language).Verdict != ConfirmationVerdict.NotConfirmed)
                    {
                        problems.Add($"{language} '{phrase}' read as {readAs.Language ?? "unknown"}: confirmation");
                    }

                    if (DeclineDetector.LeadsWithNegation(phrase, readAs.Language) || DeclineDetector.IsBareNegation(phrase, readAs.Language))
                    {
                        problems.Add($"{language} '{phrase}' read as {readAs.Language ?? "unknown"}: refusal");
                    }
                }
            }

            problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
        }
        finally
        {
            ShippedConversationSignals.Uninstall();
        }
    }

    private static (string Client, string Date, string Shift)[] SamplesFor(string language, string personLabel) =>
    [
        (NeutralClient, SampleDate, NeutralShift),
        (RealisticClient, SampleDate, LocalShiftNames[language]),
        (NumberedClient(personLabel), SampleDate, LocalShiftNames[language]),
    ];

    private static string NumberedClient(string personLabel) =>
        personLabel.Replace(NamePlaceholder, RealisticClient, StringComparison.Ordinal)
            .Replace(NumberPlaceholder, SampleEmployeeNumber, StringComparison.Ordinal);

    private static string Fill(string template, (string Client, string Date, string Shift) subject) =>
        template.Replace(ClientPlaceholder, subject.Client, StringComparison.Ordinal)
            .Replace(DatePlaceholder, subject.Date, StringComparison.Ordinal)
            .Replace(ShiftPlaceholder, subject.Shift, StringComparison.Ordinal);

    private static List<(string Language, string Template, string PersonLabel, Dictionary<string, List<string>>? PackSynonyms)> PhrasesOrInconclusive()
    {
        var catalogueDirectory = RepositoryRootLocator.FindDirectory(UiCatalogueRelativePath);
        var pluginDirectory = RepositoryRootLocator.FindDirectory(PluginLanguagesRelativePath);
        if (catalogueDirectory == null || pluginDirectory == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiCatalogueRelativePath));
        }

        var phrases = new List<(string, string, string, Dictionary<string, List<string>>?)>();
        foreach (var language in CoreLanguages)
        {
            var catalogue = Path.Combine(catalogueDirectory!, language + JsonExtension);
            phrases.Add((language, ReadText(catalogue, TriggerPhraseKey), ReadText(catalogue, ClientWithNumberKey), null));
        }

        foreach (var directory in Directory.GetDirectories(pluginDirectory!))
        {
            var translations = Path.Combine(directory, TranslationsFileName);
            var synonyms = Path.Combine(directory, SkillSynonymsFileName);
            if (!File.Exists(translations) || !File.Exists(synonyms))
            {
                continue;
            }

            phrases.Add((
                Path.GetFileName(directory),
                ReadText(translations, TriggerPhraseKey),
                ReadText(translations, ClientWithNumberKey),
                JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(synonyms))));
        }

        return phrases;
    }

    private static string ReadText(string path, string key)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty(key, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static void MergePackSynonyms(
        List<AgentSkill> catalogue, string language, Dictionary<string, List<string>> packSynonyms)
    {
        var code = language.ToLowerInvariant();
        foreach (var skill in catalogue)
        {
            if (!packSynonyms.TryGetValue(skill.Name, out var synonyms) || synonyms.Count == 0)
            {
                continue;
            }

            skill.Synonyms ??= new Dictionary<string, List<string>>();
            skill.Synonyms[code] = synonyms;
        }
    }
}
