// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TtsTextSanitizerTests
{
    [Test]
    public void Sanitize_Headings_RemovesHashMarkers()
    {
        var result = TtsTextSanitizer.Sanitize("### Die Ansicht Bestellungen\nText dazu.");

        result.ShouldBe("Die Ansicht Bestellungen\nText dazu.");
    }

    [Test]
    public void Sanitize_BoldAndItalic_KeepsContent()
    {
        var result = TtsTextSanitizer.Sanitize("Die **versiegelte Bestellung** ist *unveränderlich*.");

        result.ShouldBe("Die versiegelte Bestellung ist unveränderlich.");
    }

    [Test]
    public void Sanitize_InlineCodeAndLinks_KeepLabels()
    {
        var result = TtsTextSanitizer.Sanitize("Der Button `Speichern` öffnet [die Planung](/workplace/schedule).");

        result.ShouldBe("Der Button Speichern öffnet die Planung.");
    }

    [Test]
    public void Sanitize_ListBulletsAndBlockquotes_RemoveMarkers()
    {
        var result = TtsTextSanitizer.Sanitize("- Erster Punkt\n- Zweiter Punkt\n> Hinweis");

        result.ShouldBe("Erster Punkt\nZweiter Punkt\nHinweis");
    }

    [Test]
    public void Sanitize_CodeFences_RemoveFenceLinesButKeepText()
    {
        var result = TtsTextSanitizer.Sanitize("Vorher\n```text\nInhalt\n```\nNachher");

        result.ShouldContain("Inhalt");
        result.ShouldNotContain("```");
    }

    [Test]
    public void Sanitize_TablePipes_BecomeSpaces()
    {
        var result = TtsTextSanitizer.Sanitize("| Spalte A | Spalte B |\n|---|---|\n| Wert 1 | Wert 2 |");

        result.ShouldNotContain("|");
        result.ShouldContain("Spalte A");
        result.ShouldContain("Wert 2");
    }

    [Test]
    public void Sanitize_PlainText_StaysUntouched()
    {
        var result = TtsTextSanitizer.Sanitize("Ganz normaler Satz ohne Formatierung.");

        result.ShouldBe("Ganz normaler Satz ohne Formatierung.");
    }
}
