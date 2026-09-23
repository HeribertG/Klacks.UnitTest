// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationQuestionGuard, the code-side guard rails for a question Klacksy sends to
/// an employee: length, at most two sentences (a time like 14.00 is not a sentence end), must be a
/// question, no links, and no health term in any language (word-start match for spaced scripts,
/// substring match for Japanese, Thai and Chinese). Generic "sick" words stay allowed on purpose.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Inbound;

namespace Klacks.UnitTest.Domain.Services.Inbound;

[TestFixture]
public class ClarificationQuestionGuardTests
{
    [TestCase("Heißt das, du kannst deinen Spätdienst heute (14:00–22:00) nicht antreten?")]
    [TestCase("Heißt das, du bist heute krank und kannst den Spätdienst nicht antreten?")]
    [TestCase("Kommst du morgen um 14.00 Uhr zum Frühdienst?")]
    [TestCase("Does that mean you cannot work your late shift today (14:00-22:00)?")]
    [TestCase("Are you in Spain today, so you cannot come in?")]
    [TestCase("Cela signifie-t-il que tu ne peux pas assurer ton service de ce soir ?")]
    [TestCase("Vuol dire che oggi non puoi fare il turno serale?")]
    [TestCase("Danke für die Nachricht. Kannst du heute nicht arbeiten?")]
    public void AcceptableQuestion_Passes(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeTrue(violation);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    public void EmptyQuestion_IsRejected(string? question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.EmptyViolation);
    }

    [Test]
    public void TooLongQuestion_IsRejected()
    {
        var question = new string('a', 301) + "?";

        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.TooLongViolation);
    }

    [Test]
    public void ThreeSentences_AreRejected()
    {
        ClarificationQuestionGuard.IsAcceptable("Hallo. Danke. Kommst du heute?", out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.TooManySentencesViolation);
    }

    [Test]
    public void Statement_IsRejected()
    {
        ClarificationQuestionGuard.IsAcceptable("Du bist heute abwesend.", out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.NotAQuestionViolation);
    }

    [TestCase("Kannst du hier bestätigen: https://example.com?")]
    [TestCase("Siehe www.example.com, kommst du heute?")]
    public void Link_IsRejected(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.LinkViolation);
    }

    [TestCase("Hast du Fieber?")]
    [TestCase("Welche Symptome hast du?")]
    [TestCase("Warst du schon beim Arzt?")]
    [TestCase("Do you have a fever?")]
    [TestCase("Did the doctor say how long?")]
    [TestCase("As-tu de la fièvre ?")]
    [TestCase("Es-tu allé chez le médecin ?")]
    [TestCase("Hai la febbre?")]
    [TestCase("Sei andato dal medico?")]
    public void HealthTerm_IsRejected(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldStartWith(ClarificationQuestionGuard.HealthTermViolationPrefix);
    }

    [Test]
    public void HealthTermOfAnotherLanguage_IsAlsoRejected()
    {
        ClarificationQuestionGuard.IsAcceptable("Kommst du trotz fever heute?", out _).ShouldBeFalse();
    }

    [Test]
    public void CoreLanguages_HaveAtLeastTwelveTermsEach()
    {
        foreach (var language in new[] { "de", "en", "fr", "it" })
        {
            ClarificationHealthTerms.ByLanguage[language].Count.ShouldBeGreaterThanOrEqualTo(12, language);
        }
    }
}
