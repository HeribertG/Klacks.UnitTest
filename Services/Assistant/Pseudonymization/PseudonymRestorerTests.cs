// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Pseudonymization;
using static Klacks.UnitTest.Services.Assistant.Pseudonymization.PseudonymTestMaps;

namespace Klacks.UnitTest.Services.Assistant.Pseudonymization;

[TestFixture]
public class PseudonymRestorerTests
{
    private PseudonymMap _map = null!;
    private List<string> _unknown = null!;

    [SetUp]
    public void SetUp()
    {
        var dictionary = Dictionary(
        [
            Employee(17, "Anna", "Meier", idNumber: 4711),
            Employee(18, "Jürg", "Müller", GenderEnum.Male),
            Employee(19, "Max", "Koch", GenderEnum.Male),
            Employee(30, "Beat", "Meier", GenderEnum.Male)
        ]);
        _map = Map(dictionary, "Tel 079 123 45 67");
        foreach (var token in new[] { "MA-0017-V", "MA-0017-P", "MA-0018-N", "NAME-0042", "TEL-0001" })
        {
            _map.MarkEmitted(token);
        }

        _unknown = new List<string>();
    }

    [Test]
    public void ExistingCodeTheRequestNeverCarried_IsNotRestored()
    {
        var fresh = Map(_map.Dictionary, "Tel 079 123 45 67");
        fresh.MarkEmitted("MA-0018-N");

        PseudonymRestorer.RestoreText("TEL-0001 MA-0017 MA-0018-P MA-0018", fresh, _unknown)
            .ShouldBe("TEL-0001 MA-0017 MA-0018-P Jürg Müller");
        _unknown.ShouldBe(["TEL-0001", "MA-0017", "MA-0018-P"]);
        PseudonymRestorer.RestoreText("MA-0017", fresh, _unknown, isToolArgument: true)
            .ShouldBe(PseudonymizationConstants.UnknownReferenceMarker + "MA-0017");
    }

    [TestCase("MA-0017 hat frei", "Anna Meier hat frei")]
    [TestCase("MA-0017-V und MA-0018-N", "Anna und Müller")]
    [TestCase("ma-17 kommt", "Anna Meier kommt")]
    [TestCase("MA0017 kommt", "Anna Meier kommt")]
    [TestCase("MA-0017s Dienst", "Anna Meiers Dienst")]
    [TestCase("NAME-0042 fehlt", "Meier fehlt")]
    [TestCase("Ruf TEL-0001 an", "Ruf 079 123 45 67 an")]
    [TestCase("MA 3 fehlen", "MA 3 fehlen")]
    [TestCase("ma-999 bleibt", "ma-999 bleibt")]
    public void RestoreText_PutsRealValuesBack(string text, string expected)
    {
        PseudonymRestorer.RestoreText(text, _map, _unknown).ShouldBe(expected);
        _unknown.ShouldBeEmpty();
    }

    [Test]
    public void UnknownCanonicalToken_InText_StaysAndIsReported()
    {
        PseudonymRestorer.RestoreText("MA-0999 ist krank", _map, _unknown).ShouldBe("MA-0999 ist krank");
        _unknown.ShouldBe(["MA-0999"]);
    }

    [Test]
    public void UnknownCanonicalToken_InToolArgument_IsMarked()
    {
        var restored = PseudonymRestorer.RestoreText("MA-0999", _map, _unknown, isToolArgument: true);

        restored.ShouldBe(PseudonymizationConstants.UnknownReferenceMarker + "MA-0999");
    }

    [Test]
    public void LastNameVariant_InWriteArgument_RestoresOnlyTheLastName()
    {
        var json = PseudonymRestorer.RestoreJsonArguments("{\"lastName\":\"MA-0017-N\",\"firstName\":\"MA-0017-V\"}", _map, _unknown);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("lastName").GetString().ShouldBe("Meier");
        document.RootElement.GetProperty("firstName").GetString().ShouldBe("Anna");
    }

    [Test]
    public void FreeTextArguments_StayUntouched()
    {
        var json = PseudonymRestorer.RestoreJsonArguments(
            "{\"note\":\"Koch-Dienst, tel 044 123 45 67, MA 3 da\",\"lastName\":\"Müller\"}", _map, _unknown);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("note").GetString().ShouldBe("Koch-Dienst, tel 044 123 45 67, MA 3 da");
        document.RootElement.GetProperty("lastName").GetString().ShouldBe("Müller");
        _unknown.ShouldBeEmpty();
    }

    [Test]
    public void PersonnelNumberToken_BecomesNumberInJson()
    {
        var json = PseudonymRestorer.RestoreJsonArguments("{\"idNumber\":\"MA-0017-P\",\"text\":\"#MA-0017-P\"}", _map, _unknown);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("idNumber").GetInt32().ShouldBe(4711);
        document.RootElement.GetProperty("text").GetString().ShouldBe("#4711");
    }

    [Test]
    public void UnparseableJson_IsReturnedUnchanged()
    {
        PseudonymRestorer.RestoreJsonArguments("{\"name\":\"MA-0017", _map, _unknown).ShouldBe("{\"name\":\"MA-0017");
    }

    [Test]
    public void RestoreParameters_HandlesStringsAndNestedJsonElements()
    {
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "MA-0018",
            ["filter"] = JsonSerializer.SerializeToElement(new { people = new[] { "MA-0017-N", "MA-0999" }, count = 2 })
        };

        var restored = PseudonymRestorer.RestoreParameters(parameters, _map, _unknown);

        restored["name"].ShouldBe("Jürg Müller");
        var filter = (JsonElement)restored["filter"];
        filter.GetProperty("people")[0].GetString().ShouldBe("Meier");
        filter.GetProperty("people")[1].GetString().ShouldBe(PseudonymizationConstants.UnknownReferenceMarker + "MA-0999");
        filter.GetProperty("count").GetInt32().ShouldBe(2);
        parameters["name"].ShouldBe("MA-0018");
    }

    [TestCase("Plane Anna Meier statt Meier ein")]
    [TestCase("{\"Name\":\"Koch\",\"Note\":\"Koch-Dienst bei Max\"}")]
    [TestCase("Ruf 079 123 45 67 an, Beat Meier hat frei.")]
    public void RoundTrip_ReturnsTheOriginal(string text)
    {
        var dictionary = _map.Dictionary;
        var map = Map(dictionary, text);

        var pseudonymized = PseudonymTextPseudonymizer.Pseudonymize(text, map, new HashSet<string>(), Today);
        pseudonymized.ShouldNotContain("Meier");
        pseudonymized.ShouldNotContain("079");

        PseudonymRestorer.RestoreText(pseudonymized, map, _unknown).ShouldBe(text);
    }
}
