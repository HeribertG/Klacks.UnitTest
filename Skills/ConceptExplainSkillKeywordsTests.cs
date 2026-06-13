// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ConceptExplainSkillKeywords — verifies that concept keywords in the user
/// message resolve to the matching explain skill independent of casing and phrasing, and
/// that unrelated messages resolve to nothing.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConceptExplainSkillKeywordsTests
{
    [TestCase("Kannst du mir Bestellungen erklären!")]
    [TestCase("was ist eine bestellung?")]
    [TestCase("Warum kann ich nach dem Versiegeln nichts mehr ändern?")]
    [TestCase("What does a sealed order mean?")]
    public void OrderConceptPhrases_ResolveLifecycleSkill(string message)
    {
        var result = ConceptExplainSkillKeywords.ResolveSkillNames(message);

        Assert.That(result, Does.Contain(SkillNames.ExplainShiftLifecycle));
    }

    [TestCase("Wie viele Mitarbeiter haben wir?")]
    [TestCase("Erstelle einen neuen Mitarbeiter")]
    [TestCase("")]
    [TestCase(null)]
    public void UnrelatedMessages_ResolveNothing(string? message)
    {
        var result = ConceptExplainSkillKeywords.ResolveSkillNames(message);

        Assert.That(result, Is.Empty);
    }

    [TestCase("erkläre mir den dashbord!", "explain_page_dashboard")]
    [TestCase("Erkläre mir das Dashboard", "explain_page_dashboard")]
    [TestCase("erkläre mir Übersicht", "explain_page_dashboard")]
    [TestCase("Was ist ein sporadischer Einsatz?", "explain_shift_sporadic")]
    [TestCase("Was ist ein Zeitfenster-Dienst?", "explain_shift_time_range")]
    [TestCase("Wie funktioniert der Einsatzplan?", "explain_page_schedule")]
    [TestCase("Was zeigt der Posteingang?", "explain_page_inbox")]
    [TestCase("Erkläre mir die Schichtvorlage", "explain_shift_container")]
    [TestCase("erkläre mir die Dienste", "explain_page_shifts")]
    public void PageAndConceptPhrases_ResolveTheirExplainSkill(string message, string expectedSkill)
    {
        var result = ConceptExplainSkillKeywords.ResolveSkillNames(message);

        Assert.That(result, Does.Contain(expectedSkill));
    }

    [Test]
    public void MultipleKeywordsForSameSkill_ResolveDistinct()
    {
        var result = ConceptExplainSkillKeywords.ResolveSkillNames(
            "Bestellung versiegeln — was passiert da?");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Is.EqualTo(SkillNames.ExplainShiftLifecycle));
    }
}
