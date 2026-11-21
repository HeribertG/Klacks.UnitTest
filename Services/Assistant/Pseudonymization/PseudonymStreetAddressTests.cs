// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

/// <summary>
/// Street addresses: street and house number become ADR tokens in every common spelling, while a street without a
/// number, another house number, postcode and town stay readable; the tokens restore to the stored street text.
/// </summary>
[TestFixture]
public class PseudonymStreetAddressTests
{
    private PseudonymDictionary _dictionary = null!;

    [SetUp]
    public void SetUp()
    {
        _dictionary = Dictionary(
            [Employee(17, "Anna", "Meier"), Employee(18, "Pierre", "Fatio", GenderEnum.Male)],
            addresses:
            [
                Address(3, "Bahnhofstrasse 12", "c/o Spitex Grabs"),
                Address(4, "Rue Pierre Fatio 7"),
                Address(5, "12 rue de Lausanne"),
                Address(6, "Hauptstraße 5a"),
                Address(7, "Route de Berne 20", "Postfach 44"),
                Address(8, "Bahnhofstrasse")
            ]);
    }

    [TestCase("Anna wohnt an der Bahnhofstrasse 12 in 9472 Grabs.", "MA-0017-V wohnt an der ADR-0003 in 9472 Grabs.")]
    [TestCase("Adresse: Bahnhofstr. 12, 9472 Grabs", "Adresse: ADR-0003, 9472 Grabs")]
    [TestCase("Adresse: Bahnhofstr 12", "Adresse: ADR-0003")]
    [TestCase("Adresse: Bahnhofstraße 12", "Adresse: ADR-0003")]
    [TestCase("Adresse: BAHNHOFSTRASSE 12", "Adresse: ADR-0003")]
    [TestCase("Adresse: Hauptstrasse 5a", "Adresse: ADR-0006")]
    [TestCase("Adresse: Hauptstr. 5A", "Adresse: ADR-0006")]
    [TestCase("Adresse: Rte de Berne 20", "Adresse: ADR-0007")]
    [TestCase("Brief an Postfach 44", "Brief an ADR-0007-A2")]
    [TestCase("Habite 12 rue de Lausanne, Genève", "Habite ADR-0005, Genève")]
    public void StoredStreet_IsReplacedInItsSpellings(string text, string expected)
    {
        Pseudonymize(_dictionary, text).ShouldBe(expected);
    }

    [TestCase("Die Bahnhofstrasse ist gesperrt.")]
    [TestCase("Bahnhofstrasse 13 ist eine andere Adresse.")]
    [TestCase("Gruppe Grabs, PLZ 9472, Kanton SG")]
    [TestCase("Dienst am 2026-11-12 von 08:00 bis 12:00")]
    [TestCase("Bahnhofstrasse, 12 Personen")]
    public void StreetWithoutItsNumber_TownAndPostcode_StayReadable(string text)
    {
        Pseudonymize(_dictionary, text).ShouldBe(text);
    }

    [Test]
    public void StreetNamedAfterAPerson_BecomesOneAddressToken()
    {
        Pseudonymize(_dictionary, "Pierre Fatio wohnt an der Rue Pierre Fatio 7.")
            .ShouldBe("MA-0018 wohnt an der ADR-0004.");
    }

    [Test]
    public void JsonToolResult_WithEscapedUmlaut_IsMatched()
    {
        var json = "{\"street\":\"Hauptstra\\u00DFe 5a\",\"zip\":\"9472\",\"city\":\"Grabs\"}";

        Pseudonymize(_dictionary, json).ShouldBe("{\"street\":\"ADR-0006\",\"zip\":\"9472\",\"city\":\"Grabs\"}");
    }

    [Test]
    public void LineWithoutHouseNumber_GetsNoKey()
    {
        PseudonymStreets.Parse("Bahnhofstrasse").ShouldBeNull();
        PseudonymStreets.Parse("c/o Spitex Grabs").ShouldBeNull();
        PseudonymStreets.Parse("Bahnhofstrasse 12, 3. Stock")!.Surface.ShouldBe("Bahnhofstrasse 12");
    }

    [Test]
    public void PostcodeAndTownLines_NeverBecomeAStreetKey()
    {
        var dictionary = Dictionary(
            [],
            addresses:
            [
                Address(3, "Bahnhofstrasse 12", "9472 Grabs"),
                new PseudonymAddress("4", "Seeweg 3", null, null, "c/o Meier", "CH-9472 Grabs")
            ]);

        PseudonymStreets.Parse("9472 Grabs").ShouldBeNull();
        PseudonymStreets.Parse("CH-9472 Grabs").ShouldBeNull();
        Pseudonymize(dictionary, "Bahnhofstrasse 12, 9472 Grabs").ShouldBe("ADR-0003, 9472 Grabs");
        Pseudonymize(dictionary, "Seeweg 3, CH-9472 Grabs").ShouldBe("ADR-0004, CH-9472 Grabs");
        Pseudonymize(dictionary, "Gruppe 9472 Grabs").ShouldBe("Gruppe 9472 Grabs");
    }

    [Test]
    public void SharedStreet_TakesTheLowestAddressId()
    {
        var dictionary = Dictionary([], addresses: [Address(9, "Seeweg 3"), Address(8, "Seeweg 3")]);

        Pseudonymize(dictionary, "Seeweg 3").ShouldBe("ADR-0008");
    }

    [Test]
    public void AddressToken_RestoresToTheStoredStreet_InTextAndToolArguments()
    {
        const string text = "Bahnhofstr. 12, Postfach 44";
        var map = Map(_dictionary, text);
        var used = new HashSet<string>();
        PseudonymTextPseudonymizer.Pseudonymize(text, map, used, Today).ShouldBe("ADR-0003, ADR-0007-A2");
        map.HasEmitted.ShouldBeTrue();
        var unknown = new List<string>();

        PseudonymRestorer.RestoreText("Adresse ADR-0003 und adr-3", map, unknown).ShouldBe("Adresse Bahnhofstrasse 12 und Bahnhofstrasse 12");
        PseudonymRestorer.RestoreJsonArguments("{\"street\":\"ADR-0007-A2\"}", map, unknown).ShouldBe("{\"street\":\"Postfach 44\"}");
        unknown.ShouldBeEmpty();
    }

    [Test]
    public void UnknownOrNotEmittedAddressToken_IsMarkedInToolArguments()
    {
        var map = Map(_dictionary);
        map.MarkEmitted("ADR-0003");
        var unknown = new List<string>();

        PseudonymRestorer.RestoreText("ADR-0004", map, unknown, isToolArgument: true)
            .ShouldBe(PseudonymizationConstants.UnknownReferenceMarker + "ADR-0004");
        PseudonymRestorer.RestoreText("ADR-0999", map, unknown, isToolArgument: true)
            .ShouldBe(PseudonymizationConstants.UnknownReferenceMarker + "ADR-0999");
        unknown.ShouldBe(["ADR-0004", "ADR-0999"]);
    }

    [Test]
    public void AddressTokenSplitAcrossStreamChunks_IsRestored()
    {
        var map = Map(_dictionary);
        map.MarkEmitted("ADR-0003");
        var restorer = new PseudonymStreamRestorer(map, new List<string>());

        var output = new[] { "Wohnt an AD", "R-00", "03, Grabs" }.SelectMany(restorer.Push).ToList();
        output.AddRange(restorer.Flush());

        string.Concat(output).ShouldBe("Wohnt an Bahnhofstrasse 12, Grabs");
    }

    [Test]
    public void OnlyAnAddress_StillAddsTheLegend()
    {
        var map = Map(_dictionary, "Bahnhofstrasse 12");
        var used = new HashSet<string>();
        PseudonymTextPseudonymizer.Pseudonymize("Bahnhofstrasse 12", map, used, Today);

        PseudonymLegend.Build(used, map.HasEmitted, _dictionary).ShouldNotBeNull().ShouldContain("ADR");
    }
}
