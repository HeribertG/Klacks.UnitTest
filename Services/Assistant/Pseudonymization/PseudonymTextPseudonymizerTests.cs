// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

[TestFixture]
public class PseudonymTextPseudonymizerTests
{
    private PseudonymDictionary _dictionary = null!;

    [SetUp]
    public void SetUp()
    {
        _dictionary = Dictionary(
        [
            Employee(17, "Anna", "Meier", idNumber: 4711),
            Employee(18, "Jürg", "Müller", GenderEnum.Male),
            Employee(19, "Max", "Koch", GenderEnum.Male),
            Employee(20, "Li", "Wu"),
            Customer(21, "Hans", "Brunner"),
            User(22, "Anna", "Meier"),
            User(23, "Petra", "Steiner")
        ]);
    }

    [TestCase("Plane Anna Meier ein", "Plane MA-0017 ein")]
    [TestCase("Meier hat frei", "MA-0017-N hat frei")]
    [TestCase("Anna kommt", "MA-0017-V kommt")]
    [TestCase("Liste: Meier, Anna", "Liste: MA-0017-R")]
    [TestCase("Meier Anna", "MA-0017-S")]
    [TestCase("Brunner bestellt", "KL-0021-N bestellt")]
    [TestCase("Petra Steiner plant", "BE-0023 plant")]
    public void KnownNames_BecomeTheirVariantToken(string text, string expected)
    {
        Pseudonymize(_dictionary, text).ShouldBe(expected);
    }

    [TestCase("MÜLLER")]
    [TestCase("Mueller")]
    [TestCase("muller")]
    [TestCase("Müller")]
    public void CaseAndUmlautSpellings_AllMatch(string spelling)
    {
        Pseudonymize(_dictionary, $"Dienst für {spelling}.").ShouldBe("Dienst für MA-0018-N.");
    }

    [Test]
    public void JsonEscapedName_IsMatched()
    {
        var json = "{\"Name\":\"M\\u00FCller\",\"FirstName\":\"J\\u00FCrg\"}";

        var result = Pseudonymize(_dictionary, json);

        result.ShouldBe("{\"Name\":\"MA-0018-N\",\"FirstName\":\"MA-0018-V\"}");
    }

    [Test]
    public void EscapedQuoteAndBackslash_StayEscaped()
    {
        var json = "{\"a\":\"x\\u0022y\",\"b\":\"\\\\u00FC\"}";

        Pseudonymize(_dictionary, json).ShouldBe(json);
    }

    [TestCase("Meierhof")]
    [TestCase("Anneliese")]
    [TestCase("Koch-Dienst")]
    [TestCase("Li und Wu")]
    public void NoWholeWordOrTooShort_StaysUnchanged(string text)
    {
        Pseudonymize(_dictionary, text).ShouldBe(text);
    }

    [Test]
    public void FullNameOfEmployee_WinsOverUserWithSameName()
    {
        var used = new HashSet<string>();

        Pseudonymize(_dictionary, "Anna Meier", used).ShouldBe("MA-0017");
        used.ShouldContain("MA-0017");
    }

    [Test]
    public void NameOfTwoEmployees_BecomesAmbiguousNamePseudonym()
    {
        var dictionary = Dictionary([Employee(17, "Anna", "Meier"), Employee(30, "Beat", "Meier")]);

        var result = Pseudonymize(dictionary, "Plane Anna statt Meier ein");

        result.ShouldBe($"Plane MA-0017-V statt NAME-00{FirstAmbiguousOrdinal} ein");
    }

    [Test]
    public void LongestPhraseWins()
    {
        Pseudonymize(_dictionary, "Anna Meier und Meier").ShouldBe("MA-0017 und MA-0017-N");
    }

    [Test]
    public void Patterns_AreReplaced_AndDatesTimesAndIdsAreNot()
    {
        const string text = "Mail anna.meier@example.ch, Tel +41 44 123 45 67 oder 079 123 45 67. " +
                            "Dienst 2026-11-20 08:00-17:00, Id 3f2a1b4c-0d5e-4f6a-8b7c-044665544000.";

        var result = Pseudonymize(_dictionary, text);

        result.ShouldBe("Mail MAIL-0001, Tel TEL-0001 oder TEL-0002. " +
                        "Dienst 2026-11-20 08:00-17:00, Id 3f2a1b4c-0d5e-4f6a-8b7c-044665544000.");
    }

    [Test]
    public void StoredPhoneWithoutPrefix_IsReplaced()
    {
        var dictionary = Dictionary([Employee(17, "Anna", "Meier")], ["+41 44 123 45 67"]);

        Pseudonymize(dictionary, "{\"Prefix\":\"+41\",\"Value\":\"441234567\"}")
            .ShouldBe("{\"Prefix\":\"+41\",\"Value\":\"TEL-0001\"}");
    }

    [Test]
    public void PersonnelNumber_FieldAndCandidateSuffix_BecomePToken()
    {
        Pseudonymize(_dictionary, "{\"IdNumber\":4711,\"Name\":\"Meier\"}")
            .ShouldBe("{\"IdNumber\":\"MA-0017-P\",\"Name\":\"MA-0017-N\"}");
        Pseudonymize(_dictionary, "- Meier, Anna (#4711)").ShouldBe("- MA-0017-R (#MA-0017-P)");
        Pseudonymize(_dictionary, "Wer ist #4711?").ShouldBe("Wer ist #MA-0017-P?");
        Pseudonymize(_dictionary, "Ticket #47110").ShouldBe("Ticket #47110");
    }

    [Test]
    public void UnknownPersonnelNumber_StaysUnchanged()
    {
        Pseudonymize(_dictionary, "{\"idNumber\":99}").ShouldBe("{\"idNumber\":99}");
    }

    [Test]
    public void Birthdate_BecomesAge()
    {
        Pseudonymize(_dictionary, "{\"Birthdate\":\"1980-11-21T00:00:00\"}").ShouldBe("{\"Birthdate\":\"age 45\"}");
        Pseudonymize(_dictionary, "{\"birthDate\":\"1980-11-20\"}").ShouldBe("{\"birthDate\":\"age 46\"}");
    }

    [Test]
    public void NameInsideMailAddress_StaysPartOfTheMailToken()
    {
        Pseudonymize(_dictionary, "an meier@firma.ch").ShouldBe("an MAIL-0001");
    }

    [Test]
    public void SystemPromptMode_LeavesLowercaseCommonWordsButReplacesCapitalisedNames()
    {
        var dictionary = Dictionary([Employee(40, "Can", "Yilmaz", GenderEnum.Male)]);
        var map = Map(dictionary);

        PseudonymTextPseudonymizer.Pseudonymize("You can ask Can or can yilmaz.", map, new HashSet<string>(), Today, requireCapitalSingleWord: true)
            .ShouldBe("You can ask MA-0040-V or MA-0040.");
        PseudonymTextPseudonymizer.Pseudonymize("plane can ein", map, new HashSet<string>(), Today)
            .ShouldBe("plane MA-0040-V ein");
    }

    [TestCase("Annas Dienst", "MA-0017-Vs Dienst")]
    [TestCase("Meiers Schicht", "MA-0017-Ns Schicht")]
    [TestCase("Klaus kommt", "Klaus kommt")]
    public void GermanGenitive_ReplacesTheNameAndKeepsTheS(string text, string expected)
    {
        Pseudonymize(_dictionary, text).ShouldBe(expected);
    }

    [Test]
    public void KlacksAuthoredText_LeavesLowercaseWordsThatAreAlsoNames()
    {
        var dictionary = Dictionary([Employee(41, "Ines", "Kurz")]);
        var map = Map(dictionary);
        var request = new Klacks.Api.Domain.Services.Assistant.Providers.LLMProviderRequest
        {
            Message = "{\"hint\":\"Antworte kurz.\"}",
            IsKlacksAuthoredText = true
        };

        var copy = PseudonymRequestCopier.Copy(
            request,
            text => PseudonymTextPseudonymizer.Pseudonymize(text, map, new HashSet<string>(), Today),
            text => PseudonymTextPseudonymizer.Pseudonymize(text, map, new HashSet<string>(), Today, requireCapitalSingleWord: true));

        copy.Message.ShouldBe("{\"hint\":\"Antworte kurz.\"}");
    }
}
