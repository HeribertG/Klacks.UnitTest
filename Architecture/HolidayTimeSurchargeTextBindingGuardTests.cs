// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Binds what Klacksy and the manuals say about the "time surcharge when working" switch (CalendarRule.IsPaid) to
/// what the code does. The statement "only an official holiday marked for the time surcharge earns it" is true only
/// while MacroDataProvider feeds the surcharge flag from IHolidaysListCalculator.IsPaidOfficialHoliday; before that
/// release the old texts ("nothing evaluates it") were the true ones. Whichever way the code points, the knowledge
/// file, the ontology and every language pack's manuals must say the same - a text that runs ahead of or behind the
/// shipped code makes Klacksy explain behaviour the product does not have.
/// </summary>

using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class HolidayTimeSurchargeTextBindingGuardTests
{
    private const string SurchargePredicate = "IsPaidOfficialHoliday";
    private const string KnowledgeCurrentMarker = "marked for the time surcharge";
    private const string KnowledgeOutdatedMarker = "nothing evaluates it";
    private const string OntologyCurrentMarker = "IsPaid = holiday time surcharge when worked, official days only";
    private const string ManualPaidRowMarker = "data-holiday-field=\"is-paid\"";
    private const string MacroHolidayRowMarker = "data-macro-variable=\"holiday\"";
    private const string MacroHolidayNextDayRowMarker = "data-macro-variable=\"holidaynextday\"";
    private const string CalendarManualFileName = "calendar-rule-manual.html";
    private const string MacroManualFileName = "macro-manual.html";

    private static string ApiFile(params string[] relative) =>
        File.ReadAllText(Path.Combine([RepositoryRootLocator.ApiProject, .. relative]));

    private static bool CodeHonoursIsPaid() =>
        ApiFile("Infrastructure", "Services", "Macros", "MacroDataProvider.cs").Contains(SurchargePredicate);

    private static string KnowledgeFile() =>
        ApiFile("Infrastructure", "Persistence", "Seed", "KlacksyKnowledge", "42-calendar-model.md");

    private static string OntologyFile() =>
        ApiFile("Application", "Services", "Assistant", "Ontology", "KlacksOntologyService.cs");

    private static IReadOnlyList<string> PackDocsDirectories()
    {
        var languages = Path.Combine(RepositoryRootLocator.ApiProject, "Plugins", "Languages");
        return Directory.GetDirectories(languages)
            .Select(directory => Path.Combine(directory, "docs"))
            .Where(docs => File.Exists(Path.Combine(docs, CalendarManualFileName)))
            .ToList();
    }

    [Test]
    public void TheGuard_ScansEveryLanguagePackThatShipsTheManuals()
    {
        PackDocsDirectories().Count.ShouldBeGreaterThanOrEqualTo(21);
    }

    [Test]
    public void KnowledgeFile_MatchesTheShippedSurchargeRule()
    {
        var knowledge = KnowledgeFile();

        if (CodeHonoursIsPaid())
        {
            knowledge.ShouldContain(KnowledgeCurrentMarker);
            knowledge.ShouldNotContain(KnowledgeOutdatedMarker);
        }
        else
        {
            knowledge.ShouldNotContain(KnowledgeCurrentMarker);
        }
    }

    [Test]
    public void Ontology_MatchesTheShippedSurchargeRule()
    {
        OntologyFile().Contains(OntologyCurrentMarker).ShouldBe(CodeHonoursIsPaid());
    }

    [Test]
    public void EveryPackManual_MatchesTheShippedSurchargeRule()
    {
        var expected = CodeHonoursIsPaid();
        var mismatches = new List<string>();

        foreach (var docs in PackDocsDirectories())
        {
            var calendar = File.ReadAllText(Path.Combine(docs, CalendarManualFileName));
            var macro = File.ReadAllText(Path.Combine(docs, MacroManualFileName));
            var rewritten = calendar.Contains(ManualPaidRowMarker)
                            && macro.Contains(MacroHolidayRowMarker)
                            && macro.Contains(MacroHolidayNextDayRowMarker);
            if (rewritten != expected)
            {
                mismatches.Add(docs);
            }
        }

        mismatches.ShouldBeEmpty(
            "These language packs describe the holiday time surcharge differently from the shipped code: " +
            string.Join(", ", mismatches));
    }
}
