// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AffirmationDetector — verifies it fires on short go-ahead replies across
/// languages and, crucially (precision bias), stays silent when any negation token appears
/// anywhere or the message ends in a question, so an outstanding confirmation is never forced
/// to execute on an ambiguous or negative reply.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class AffirmationDetectorTests
{
    [TestCase("ja")]
    [TestCase("Ja")]
    [TestCase("Ja bitte")]
    [TestCase("Ja, ausführen")]
    [TestCase("ok")]
    [TestCase("okay")]
    [TestCase("mach das")]
    [TestCase("machen wir das")]
    [TestCase("weiter")]
    [TestCase("los")]
    [TestCase("genau, passt")]
    [TestCase("bestätige")]
    [TestCase("yes")]
    [TestCase("do it")]
    [TestCase("go ahead")]
    [TestCase("proceed")]
    [TestCase("oui")]
    [TestCase("si")]
    [TestCase("certo")]
    public void IsAffirmation_True_For_Clear_GoAheads(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeTrue(message);
    }

    [TestCase("nein")]
    [TestCase("nein danke")]
    [TestCase("ja, aber nicht heute")]
    [TestCase("ja aber das nicht")]
    [TestCase("doch nicht")]
    [TestCase("stop")]
    [TestCase("abbrechen")]
    [TestCase("warte noch")]
    [TestCase("no")]
    [TestCase("not now")]
    [TestCase("cancel")]
    [TestCase("non")]
    public void IsAffirmation_False_When_Negation_Anywhere(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeFalse(message);
    }

    [TestCase("ja? was kostet das")]
    [TestCase("ok, aber wie viele sind das?")]
    [TestCase("Wie geht das?")]
    public void IsAffirmation_False_When_Message_Is_A_Question(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeFalse(message);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("was kostet ein Dienst")]
    [TestCase("erstelle einen Kunden")]
    [TestCase("lösche die Bestellung")]
    public void IsAffirmation_False_For_Empty_Or_NonAffirmative(string message)
    {
        AffirmationDetector.IsAffirmation(message).ShouldBeFalse(message);
    }

    [Test]
    public void IsAffirmation_False_For_Null()
    {
        AffirmationDetector.IsAffirmation(null).ShouldBeFalse();
    }
}
