// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard: the clarification health-term list covers every language Klacks ships (the four
/// core languages plus every language pack directory under Klacks.Api/Plugins/Languages), with at least
/// MinimumTermsPerLanguage lower-case, trimmed terms per language, minimum term lengths per match mode
/// (word-start and compound terms at least four characters, substring terms of the scripts without word
/// boundaries at least two), match-mode sets that only name listed languages, and no generic "sick" word
/// that would block the intended attendance question. The allowed absence phrases, the harmless word parts and
/// the whole-word terms are lower case, trimmed and NFC-normalised as well (whole-word terms are letters
/// only and at least three characters), every harmless word part must still neutralise a listed stem (it
/// contains one, or its end overlaps the start of one by at least four characters), and no month, genitive
/// month or day name (full or abbreviated) of a shipped culture may contain a health term. A new language
/// pack without health terms fails here instead of silently letting health questions through.
/// </summary>

using System.Globalization;
using System.Text;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Inbound;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ClarificationHealthTermsCoverageGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string LanguagesRelativePath = "Plugins/Languages";
    private const int MinimumShippedLanguages = 25;
    private const int MinimumTermsPerLanguage = 12;
    private const int MinimumWordStartTermLength = 4;
    private const int MinimumSubstringTermLength = 2;
    private const int MinimumWholeWordTermLength = 3;
    private const int MinimumHarmlessPartStemOverlap = 4;
    private const string IcuProbeCulture = "pt";
    private const int IcuProbeMonthIndex = 1;
    private const string IcuProbeMonthName = "fevereiro";

    private static readonly string[] AllowedAttendanceQuestions =
    [
        "Heißt das, du bist heute krank und kannst den Spätdienst nicht antreten?",
        "Does that mean you are sick and cannot work your late shift today?",
        "Cela signifie-t-il que tu es malade et ne peux pas travailler ce soir ?",
        "Vuol dire che sei malato e non puoi fare il turno di stasera?",
        "Kommst du morgen um 14.00 Uhr zum Frühdienst?"
    ];

    private static DirectoryInfo ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, LanguagesRelativePath)))
            {
                return new DirectoryInfo(candidate);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate {ApiProjectDirectory} from the test base directory.");
    }

    private static IReadOnlyList<string> ShippedLanguages()
    {
        var packs = Directory.GetDirectories(Path.Combine(ApiRoot().FullName, LanguagesRelativePath))
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);

        return MultiLanguage.CoreLanguages.Concat(packs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Test]
    public void EveryShippedLanguage_HasEnoughHealthTerms()
    {
        var languages = ShippedLanguages();
        languages.Count.ShouldBeGreaterThanOrEqualTo(MinimumShippedLanguages);

        var missing = languages
            .Where(code => !ClarificationHealthTerms.ByLanguage.TryGetValue(code, out var terms) || terms.Count < MinimumTermsPerLanguage)
            .ToList();

        missing.ShouldBeEmpty($"Languages without at least {MinimumTermsPerLanguage} health terms: {string.Join(", ", missing)}");
    }

    [Test]
    public void EveryTerm_IsLowerCaseTrimmedAndLongEnough()
    {
        var offenders = new List<string>();
        foreach (var (language, terms) in ClarificationHealthTerms.ByLanguage)
        {
            var minimumLength = ClarificationHealthTerms.SubstringMatchLanguages.Contains(language)
                ? MinimumSubstringTermLength
                : MinimumWordStartTermLength;
            offenders.AddRange(terms
                .Where(term => !IsCanonical(term) || term.Length < minimumLength)
                .Select(term => $"{language}:'{term}'"));
        }

        offenders.ShouldBeEmpty(string.Join(", ", offenders));
    }

    [Test]
    public void EveryAbsencePhraseHarmlessPartAndWholeWord_IsCanonical()
    {
        var offenders = ClarificationHealthTerms.AllowedAbsencePhrases
            .Concat(ClarificationHealthTerms.HarmlessWordParts)
            .Concat(ClarificationHealthTerms.WholeWordTerms)
            .Where(entry => !IsCanonical(entry))
            .ToList();

        offenders.ShouldBeEmpty(string.Join(", ", offenders));
    }

    [Test]
    public void EveryWholeWordTerm_IsASingleWordOfAtLeastThreeLetters()
    {
        var offenders = ClarificationHealthTerms.WholeWordTerms
            .Where(term => term.Length < MinimumWholeWordTermLength || !term.All(char.IsLetter))
            .ToList();

        offenders.ShouldBeEmpty(string.Join(", ", offenders));
    }

    [Test]
    public void EveryHarmlessWordPart_StillNeutralisesAListedStem()
    {
        var stems = ClarificationHealthTerms.ByLanguage.Values.SelectMany(terms => terms).ToList();

        var unneeded = ClarificationHealthTerms.HarmlessWordParts
            .Where(part => !stems.Any(stem => CutsStem(part, stem)))
            .ToList();

        unneeded.ShouldBeEmpty($"Harmless word parts that no longer cut any health stem: {string.Join(", ", unneeded)}");
    }

    [Test]
    public void MatchModeSets_OnlyNameListedLanguages()
    {
        var unknown = ClarificationHealthTerms.SubstringMatchLanguages
            .Concat(ClarificationHealthTerms.CompoundSubstringMatchLanguages)
            .Where(code => !ClarificationHealthTerms.ByLanguage.ContainsKey(code))
            .ToList();

        unknown.ShouldBeEmpty(string.Join(", ", unknown));
        ClarificationHealthTerms.SubstringMatchLanguages
            .Intersect(ClarificationHealthTerms.CompoundSubstringMatchLanguages, StringComparer.OrdinalIgnoreCase)
            .ShouldBeEmpty();
    }

    [Test]
    public void TheIntendedAttendanceQuestions_StayAllowed()
    {
        foreach (var question in AllowedAttendanceQuestions)
        {
            ClarificationQuestionGuard.FindHealthTerm(question.ToLowerInvariant()).ShouldBeNull(question);
        }
    }

    [Test]
    public void CalendarNamesOfEveryShippedCulture_ContainNoHealthTerm()
    {
        EnsureIcuCultureDataIsAvailable();

        var offenders = new List<string>();
        foreach (var language in ShippedLanguages())
        {
            var format = new CultureInfo(language).DateTimeFormat;
            var names = format.MonthNames
                .Concat(format.MonthGenitiveNames)
                .Concat(format.AbbreviatedMonthNames)
                .Concat(format.AbbreviatedMonthGenitiveNames)
                .Concat(format.DayNames)
                .Concat(format.AbbreviatedDayNames)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal);
            foreach (var name in names)
            {
                var term = ClarificationQuestionGuard.FindHealthTerm(name);
                if (term != null)
                {
                    offenders.Add($"{language}:'{name}' -> '{term}'");
                }
            }
        }

        offenders.ShouldBeEmpty(string.Join(", ", offenders));
    }

    private static bool IsCanonical(string entry)
        => entry == entry.Trim()
            && entry == entry.ToLowerInvariant()
            && entry == entry.Normalize(NormalizationForm.FormC);

    private static bool CutsStem(string part, string stem)
    {
        if (part.Contains(stem, StringComparison.Ordinal))
        {
            return true;
        }

        var longestOverlap = Math.Min(part.Length, stem.Length) - 1;
        for (var overlap = MinimumHarmlessPartStemOverlap; overlap <= longestOverlap; overlap++)
        {
            if (part.EndsWith(stem[..overlap], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureIcuCultureDataIsAvailable()
    {
        string? probe;
        try
        {
            probe = new CultureInfo(IcuProbeCulture).DateTimeFormat.MonthNames[IcuProbeMonthIndex];
        }
        catch (CultureNotFoundException)
        {
            probe = null;
        }

        if (!string.Equals(probe, IcuProbeMonthName, StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"ICU culture data is not available (culture '{IcuProbeCulture}' month {IcuProbeMonthIndex} is '{probe}', expected '{IcuProbeMonthName}'); the calendar-name guard cannot run.");
        }
    }
}
