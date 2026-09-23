// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationQuestionGuard, the code-side guard rails for a question Klacksy sends to
/// an employee: length, at most two sentences (a time like 14.00, a date like 24.09. and an abbreviation
/// like z. B. are not sentence ends), must end with a question mark, no links, and no health term in any
/// language (German stems match inside compounds, word-start match for other spaced scripts, substring
/// match for Japanese, Thai and Chinese; the other 21 languages are covered in
/// ClarificationQuestionGuardLanguageTests). Generic "sick" words and sick-leave phrasings stay allowed on
/// purpose, and words of the system-inserted shift context (station or shift names) are ignored by the
/// health-term check.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Inbound;

namespace Klacks.UnitTest.Domain.Services.Inbound;

[TestFixture]
public class ClarificationQuestionGuardTests
{
    private const string GreekQuestionMark = "\u037E";

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

    [TestCase("Hallo. Danke. Kommst du heute?")]
    [TestCase("Hallo. Danke. Kommst du um 14.00 Uhr?")]
    [TestCase("Hallo. Danke. Kannst du vom 24.09. bis 26.09. arbeiten?")]
    [TestCase("Hallo. Danke. Kannst du z. B. am Montag den Frühdienst übernehmen?")]
    public void ThreeSentences_AreRejected(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.TooManySentencesViolation);
    }

    [TestCase("Kannst du vom 24.09. bis 26.09. arbeiten?")]
    [TestCase("Danke. Kannst du vom 24.09. bis 26.09. arbeiten?")]
    [TestCase("Kannst du z. B. am Montag den Frühdienst übernehmen?")]
    [TestCase("Danke. Kannst du z. B. am Montag den Frühdienst übernehmen?")]
    [TestCase("Danke. Kommst du morgen um 14.00 Uhr?")]
    public void DatesTimesAndAbbreviations_DoNotEndASentence(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeTrue(violation);
    }

    [TestCase("Du bist heute abwesend.")]
    [TestCase("Du bist heute abwesend; wir planen um.")]
    [TestCase("Kommst du heute?, wir planen um.")]
    [TestCase("Kommst du heute;")]
    [TestCase("Kommst du heute" + GreekQuestionMark)]
    public void Statement_IsRejected(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.NotAQuestionViolation);
    }

    [TestCase("Μπορείς να έρθεις σήμερα;")]
    [TestCase("Μπορείς να έρθεις σήμερα" + GreekQuestionMark)]
    [TestCase("今日は来られますか？")]
    [TestCase("هل يمكنك الحضور اليوم؟")]
    public void QuestionMarkOfTheLanguage_IsAccepted(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeTrue(violation);
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
    [TestCase("Quelle maladie as-tu ?")]
    public void HealthTerm_IsRejected(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldStartWith(ClarificationQuestionGuard.HealthTermViolationPrefix);
    }

    [TestCase("Hast du Rückenschmerzen?")]
    [TestCase("Warst du beim Hausarzt?")]
    [TestCase("Warst du beim Zahnarzt?")]
    [TestCase("Hast du eine Lungenentzündung?")]
    [TestCase("Hattest du einen Arbeitsunfall?")]
    [TestCase("Bist du erkältet?")]
    [TestCase("Hast du eine Erkältung?")]
    [TestCase("Welche Krankheit hast du?")]
    public void GermanCompoundHealthTerm_IsRejected(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse();
        violation.ShouldStartWith(ClarificationQuestionGuard.HealthTermViolationPrefix);
    }

    [Test]
    public void HealthTermOfAnotherLanguage_IsAlsoRejected()
    {
        ClarificationQuestionGuard.IsAcceptable("Kommst du trotz fever heute?", out _).ShouldBeFalse();
    }

    [TestCase("Bist du krankgeschrieben und kannst den Nachtdienst nicht antreten?")]
    [TestCase("Bist du heute krankgemeldet?")]
    [TestCase("Fällst du krankheitsbedingt für den Frühdienst aus?")]
    [TestCase("Are you on sick leave and cannot work your night shift?")]
    [TestCase("Vuol dire che oggi sei in malattia e non puoi fare il turno?")]
    [TestCase("Cela veut dire que tu es en arrêt maladie aujourd'hui et ne peux pas assurer ton service ?")]
    [TestCase("Es-tu en congé maladie cette semaine ?")]
    [TestCase("Es-tu en maladie aujourd'hui ?")]
    public void SickLeavePhrasing_IsAllowed(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeTrue(violation);
    }

    [TestCase("Kannst du deinen Dienst heute antreten?")]
    [TestCase("Kannst du deine Schicht morgen übernehmen?")]
    [TestCase("Kommst du heute zum Frühdienst?")]
    [TestCase("Kannst du den Spätdienst heute arbeiten?")]
    [TestCase("Trittst du den Nachtdienst morgen an?")]
    [TestCase("Kannst du am Wochenende den Bereitschaftsdienst übernehmen?")]
    [TestCase("Übernimmst du morgen den Notfalldienst?")]
    [TestCase("Hast du heute Pikettdienst?")]
    [TestCase("Arbeitest du morgen auf der Intensivstation?")]
    [TestCase("Kannst du den Wochenenddienst tauschen?")]
    [TestCase("Bist du zur Übergabe um 06:30 da?")]
    [TestCase("Kannst du morgen für die Frühschicht einspringen?")]
    [TestCase("Ist deine Krankmeldung für die Spätschicht gedacht?")]
    [TestCase("Kommst du nach dem Urlaub am Montag wieder zur Arbeit?")]
    [TestCase("Bist du heute abwesend und kannst deine Schicht nicht antreten?")]
    [TestCase("Can you still work your early shift tomorrow?")]
    [TestCase("Will you cover the night shift on the ward today?")]
    [TestCase("Are you on call this weekend?")]
    [TestCase("Peux-tu assurer ta garde de nuit demain ?")]
    [TestCase("Viens-tu travailler ce matin ?")]
    [TestCase("Puoi fare il turno di notte domani?")]
    [TestCase("Vieni al lavoro oggi pomeriggio?")]
    public void NormalAttendanceQuestion_StaysAllowed(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeTrue(violation);
    }

    [TestCase("Kannst du deinen Frühdienst Chirurgie heute antreten?", "Frühdienst Chirurgie 06:00–14:00")]
    [TestCase("Kannst du deinen Spätdienst Spital Nord heute antreten?", "Spätdienst Spital Nord")]
    [TestCase("Can you work your DPR Diagnostic Fruehschicht today?", "DPR Diagnostic Fruehschicht")]
    public void ShiftNameWithHealthTerm_IsAllowedWhenItComesFromTheSystemContext(string question, string context)
    {
        ClarificationQuestionGuard.IsAcceptable(question, context, out var violation).ShouldBeTrue(violation);
    }

    [TestCase("Kannst du deinen Frühdienst Chirurgie heute antreten?")]
    [TestCase("Kannst du deinen Spätdienst Spital Nord heute antreten?")]
    [TestCase("Can you work your DPR Diagnostic Fruehschicht today?")]
    public void ShiftNameWithHealthTerm_IsRejectedWithoutContext(string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, null, out var violation).ShouldBeFalse();
        violation.ShouldStartWith(ClarificationQuestionGuard.HealthTermViolationPrefix);
    }

    [Test]
    public void HealthTermOutsideTheContext_IsStillRejected()
    {
        ClarificationQuestionGuard.IsAcceptable(
            "Kannst du deinen Frühdienst Chirurgie heute antreten, hast du Fieber?",
            "Frühdienst Chirurgie 06:00–14:00",
            out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.HealthTermViolationPrefix + "fieber");
    }

    [TestCase("As-tu mal de tête ?", "Service de nuit 22:00–06:00", "mal de tête")]
    [TestCase("Hai mal di testa?", "Turno di notte 22:00–06:00", "mal di testa")]
    public void ContextFunctionWords_DoNotSplitMultiWordHealthTerms(string question, string context, string term)
    {
        ClarificationQuestionGuard.IsAcceptable(question, context, out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.HealthTermViolationPrefix + term);
    }

    [Test]
    public void ContextDoesNotRelaxTheOtherRules()
    {
        ClarificationQuestionGuard.IsAcceptable(
            "Du bist im Frühdienst Chirurgie.",
            "Frühdienst Chirurgie 06:00–14:00",
            out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.NotAQuestionViolation);
    }

    [TestCase("de", "Willst du die Schicht unterbrechen?")]
    [TestCase("de", "Hast du die Schicht unterbrochen?")]
    [TestCase("pt", "Podes trabalhar no turno de 3 de fevereiro?")]
    [TestCase("en", "Will you join the hospitality team tomorrow?")]
    [TestCase("es", "¿Puedes cubrir el turno de hospitalidad mañana?")]
    [TestCase("it", "Puoi coprire il turno di ospitalità domani?")]
    [TestCase("fi", "Voitko tehdä vuoron vanhusten palvelutalossa huomenna?")]
    [TestCase("pt", "Entendes a gravidade da falta de pessoal amanhã?")]
    [TestCase("es", "¿Es embarazoso para ti cambiar el turno mañana?")]
    [TestCase("id", "Apakah kamu bisa bekerja di bagian operasional besok?")]
    [TestCase("he", "האם תהיה באירופא מחר?")]
    [TestCase("cs", "Nehodí se ti směna v pondělí?")]
    [TestCase("ar", "هل ستعمل في فرع ألمانيا غدًا؟")]
    [TestCase("ko", "의사소통 교육에 참석할 수 있나요?")]
    [TestCase("ko", "설사 늦더라도 오늘 근무할 수 있나요?")]
    [TestCase("id", "Apakah kamu bisa ikut rapat operasi gudang besok?")]
    [TestCase("ms", "Bolehkah anda hadir ke program loyalti esok?")]
    [TestCase("nl", "Is dit een ziektemelding?")]
    [TestCase("nl", "Is morgen een ziektedag voor jou?")]
    [TestCase("en", "Are you on medical leave tomorrow?")]
    [TestCase("da", "Har du sygdomsfravær i morgen?")]
    [TestCase("fi", "Onko sinulla sairauspäivä huomenna?")]
    [TestCase("de", "Ist morgen ein Krankheitstag für dich?")]
    [TestCase("pt", "Podes vir depressa amanhã?")]
    public void HarmlessWordOrAbsencePhrase_IsAccepted(string language, string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeTrue($"{language}: {violation}");
    }

    [TestCase("de", "Hast du erbrochen?")]
    [TestCase("de", "Musstest du dich erbrechen?")]
    [TestCase("en", "Do you have the flu?")]
    [TestCase("en", "Are you in pain?")]
    [TestCase("en", "Do you still have pains?")]
    [TestCase("en", "Is your knee painful?")]
    [TestCase("en", "Did you take a painkiller?")]
    [TestCase("es", "¿Tienes tos?")]
    [TestCase("pt", "Estás com dor?")]
    [TestCase("fi", "Oletko menossa leikkaukseen?")]
    [TestCase("pl", "Czy odczuwasz ból?")]
    [TestCase("pl", "Czy czujesz ból?")]
    [TestCase("vi", "Bạn có sốt không?")]
    [TestCase("vi", "Bạn có đau không?")]
    [TestCase("cs", "Měl jsi nehodu?")]
    [TestCase("ar", "هل تشعر بألم في ظهرك؟")]
    [TestCase("ko", "의사에게 다녀왔나요?")]
    [TestCase("ko", "설사를 했나요?")]
    [TestCase("id", "Apakah kamu akan dioperasi?")]
    [TestCase("ms", "Adakah anda rasa loya?")]
    [TestCase("es", "¿Fuiste al doctor?")]
    [TestCase("es", "¿Estuviste en el hospital?")]
    [TestCase("ro", "Ai fost la doctor?")]
    [TestCase("ro", "Ai fost la medic?")]
    [TestCase("fr", "Es-tu déprimé ?")]
    [TestCase("es", "¿Estás deprimido?")]
    [TestCase("pt", "Estás deprimida?")]
    [TestCase("es", "¿Has tenido embarazos?")]
    public void NarrowedOrAddedHealthTerm_IsStillRejected(string language, string question)
    {
        ClarificationQuestionGuard.IsAcceptable(question, out var violation).ShouldBeFalse(language);
        violation.ShouldStartWith(ClarificationQuestionGuard.HealthTermViolationPrefix, customMessage: language);
    }

    [Test]
    public void DecomposedUnicodeQuestion_IsNormalisedBeforeTheHealthTermCheck()
    {
        ClarificationQuestionGuard.IsAcceptable("Hast du U\u0308belkeit?", out var violation).ShouldBeFalse();
        violation.ShouldBe(ClarificationQuestionGuard.HealthTermViolationPrefix + "übelkeit");
    }

    [Test]
    public void DecomposedUnicodeContext_IsNormalisedBeforeItsWordsAreIgnored()
    {
        ClarificationQuestionGuard.IsAcceptable(
            "Kannst du morgen im Ärztehaus Nord arbeiten?",
            "Fru\u0308hdienst A\u0308rztehaus Nord",
            out var violation).ShouldBeTrue(violation);
    }

    [Test]
    public void DecomposedUnicodeText_IsNormalisedByFindHealthTerm()
    {
        ClarificationQuestionGuard.FindHealthTerm("hast du u\u0308belkeit?").ShouldBe("übelkeit");
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
