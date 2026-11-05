// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScheduleOverlayIntentResolver: a message asks to see the schedule with the availabilities or the planning commands only
/// when it has a display verb, an overlay noun (availabilities, commands, keywords) and a schedule anchor (a schedule word or
/// a date signal). Covered in de/en/fr/it; a request for the availabilities alone, a question and a message without a display
/// verb never count.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ScheduleOverlayIntentResolverTests
{
    [TestCase("Zeig mir den Dezember mit Verfügbarkeiten und Keywords")]
    [TestCase("Öffne den Dienstplan mit den Verfügbarkeiten")]
    [TestCase("Blende im Einsatzplan die Planungsbefehle ein")]
    [TestCase("Show me December with availabilities and keywords")]
    [TestCase("Open the schedule with the commands shown")]
    [TestCase("Montre-moi décembre avec les disponibilités et les commandes")]
    [TestCase("Affiche le planning avec les disponibilités")]
    [TestCase("Mostrami dicembre con le disponibilità e i comandi")]
    [TestCase("Apri il calendario con le disponibilità")]
    public void DisplayVerbOverlayNounAndScheduleAnchor_IsAnOverlayRequest(string message)
    {
        ScheduleOverlayIntentResolver.IsOverlayRequest(message).ShouldBeTrue();
    }

    [TestCase("Zeig mir die Verfügbarkeiten von Mia")]
    [TestCase("Show me the availabilities")]
    [TestCase("Zeig mir den Dezember")]
    [TestCase("Mit Verfügbarkeiten und Keywords im Dezember")]
    [TestCase("Wie zeige ich im Dezember die Verfügbarkeiten an?")]
    [TestCase("Was bedeuten die Verfügbarkeiten im Dienstplan?")]
    [TestCase("")]
    [TestCase("   ")]
    public void AnythingMissingASignal_OrAQuestion_IsNotAnOverlayRequest(string message)
    {
        ScheduleOverlayIntentResolver.IsOverlayRequest(message).ShouldBeFalse();
    }

    [Test]
    public void NullMessage_IsNotAnOverlayRequest()
    {
        ScheduleOverlayIntentResolver.IsOverlayRequest(null).ShouldBeFalse();
    }
}
