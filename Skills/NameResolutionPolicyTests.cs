// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the NameMatchPolicy of the staged NameResolution. Strict (the default) picks an entity
/// only when no character of the query has to change: normalized equality (case, diacritics, punctuation),
/// a forward match on whole words ("Nord" finds "Region Nord", not "Nordwest"),
/// compact equality ("All-Shift" = "AllShift") and label-word stripping ("Gruppe X"). A substring ending
/// inside a word and a typo, token or phonetic hit becomes a Suggested outcome (one entity) or stays Ambiguous
/// (several), never a Match, also when several name variants of one entity only matched fuzzily. Lenient
/// keeps the previous behaviour, so "Region South" still resolves to "Region North" for read-only skills.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class NameResolutionPolicyTests
{
    private static readonly string[] GroupLabelWords = ["gruppe", "group"];
    private static readonly string[] MacroLabelWords = ["makro", "macro"];

    private sealed record Named(string Name);

    private sealed record MultiNamed(string Key, string[] Names);

    private static NameResolutionResult<Named> Resolve(
        IEnumerable<string> names, string query, NameMatchPolicy? policy = null, string[]? labelWords = null)
    {
        var items = names.Select(name => new Named(name)).ToList();
        return policy is { } explicitPolicy
            ? NameResolution.Resolve(items, item => item.Name, query, labelWords, explicitPolicy)
            : NameResolution.Resolve(items, item => item.Name, query, labelWords);
    }

    [Test]
    public void RegionSouth_IsSuggestedUnderStrict_AndMatchedUnderLenient()
    {
        string[] names = ["Region North", "Zürich"];

        var strict = Resolve(names, "Region South");
        var lenient = Resolve(names, "Region South", NameMatchPolicy.Lenient);

        strict.Match.ShouldBeNull();
        strict.Suggestion!.Name.ShouldBe("Region North");
        strict.Candidates.ShouldBeEmpty();
        lenient.Match!.Name.ShouldBe("Region North");
        lenient.Suggestion.ShouldBeNull();
    }

    [TestCase("zurich", "Zürich")]
    [TestCase("ZÜRICH", "Zürich")]
    [TestCase("Region  North", "Region North")]
    [TestCase("Region North!", "Region North")]
    public void NormalizedEquality_IsAcceptedUnderStrict(string query, string expected)
    {
        var result = Resolve(["Region North", "Zürich", "Bern"], query);

        result.Match!.Name.ShouldBe(expected);
        result.Suggestion.ShouldBeNull();
    }

    [TestCase("All-Shift")]
    [TestCase("All Shift")]
    [TestCase("Makro-All-Shift")]
    [TestCase("AllShift Makro")]
    public void CompactEquality_AndMacroLabelStripping_AreAcceptedUnderStrict(string query)
    {
        var result = Resolve(["AllShift", "Ferien", "NachtZuschlag"], query, labelWords: MacroLabelWords);

        result.Match!.Name.ShouldBe("AllShift");
        result.Suggestion.ShouldBeNull();
    }

    [TestCase("Gruppenleitung", "Leitung")]
    [TestCase("Leitunggruppe", "Leitung")]
    public void LabelWordGluedToTheName_IsNotStrippedUnderStrict(string query, string suggested)
    {
        var result = Resolve([suggested, "Bern"], query, labelWords: ["gruppe", "gruppen", "group"]);

        result.Match.ShouldBeNull();
        result.Suggestion!.Name.ShouldBe(suggested);
    }

    [TestCase("Gruppe Leitung")]
    [TestCase("Leitung Gruppe")]
    [TestCase("Gruppen-Leitung")]
    public void LabelWordAsAWholeWord_IsStrippedUnderStrict(string query)
    {
        var result = Resolve(["Leitung", "Bern"], query, labelWords: ["gruppe", "gruppen", "group"]);

        result.Match!.Name.ShouldBe("Leitung");
    }

    [TestCase("Makro-All-Shift")]
    [TestCase("AllShift-Makro")]
    public void HyphenatedMacroLabel_IsStrippedUnderStrict(string query)
    {
        var result = Resolve(["AllShift", "Ferien"], query, labelWords: MacroLabelWords);

        result.Match!.Name.ShouldBe("AllShift");
    }

    [Test]
    public void GroupLabelStripping_IsAcceptedUnderStrict()
    {
        var result = Resolve(["Region North", "Region South Ost"], "Gruppe Region North", labelWords: GroupLabelWords);

        result.Match!.Name.ShouldBe("Region North");
    }

    [Test]
    public void UniqueForwardSubstring_IsAcceptedUnderStrict()
    {
        var result = Resolve(["Deutschschweiz Zürich", "Bern"], "Zürich");

        result.Match!.Name.ShouldBe("Deutschschweiz Zürich");
    }

    [TestCase("Nord", "Region Nord")]
    [TestCase("Nord", "Nord-West")]
    [TestCase("Region Nord", "Grossregion Region Nord Ost")]
    public void WholeWordForwardMatch_IsAcceptedUnderStrict(string query, string expected)
    {
        var result = Resolve([expected, "Bern"], query);

        result.Match!.Name.ShouldBe(expected);
        result.Suggestion.ShouldBeNull();
    }

    [TestCase("Nord", "Nordwest")]
    [TestCase("Region Nor", "Region North")]
    [TestCase("ürich", "Deutschschweiz Zürich")]
    public void SubstringEndingInsideAWord_IsOnlySuggestedUnderStrict(string query, string suggested)
    {
        string[] names = [suggested, "Bern"];

        var strict = Resolve(names, query);
        var lenient = Resolve(names, query, NameMatchPolicy.Lenient);

        strict.Match.ShouldBeNull();
        strict.Suggestion!.Name.ShouldBe(suggested);
        lenient.Match!.Name.ShouldBe(suggested);
    }

    [Test]
    public void WholeWordMatch_WinsOverAPartialWordMatch_UnderStrict()
    {
        var result = Resolve(["Nordwest", "Region Nord"], "Nord");

        result.Match!.Name.ShouldBe("Region Nord");
    }

    [Test]
    public void SeveralForwardSubstrings_StayAmbiguousUnderStrict()
    {
        var result = Resolve(["Region North", "Region South", "Bern"], "Region");

        result.Match.ShouldBeNull();
        result.Suggestion.ShouldBeNull();
        result.Candidates.Select(item => item.Name).ShouldBe(["Region North", "Region South"], ignoreOrder: true);
    }

    [Test]
    public void SeveralFuzzyHits_StayAmbiguousUnderStrict()
    {
        var result = Resolve(["AllShift", "AllShift2", "Ferien"], "Makro All Schift", labelWords: MacroLabelWords);

        result.Match.ShouldBeNull();
        result.Suggestion.ShouldBeNull();
        result.Candidates.Select(item => item.Name).ShouldBe(["AllShift", "AllShift2"], ignoreOrder: true);
    }

    [TestCase("Zürih", "Zürich")]
    [TestCase("Mayer", "Meier")]
    [TestCase("Gruppe Zürich Team", "Zürich")]
    public void TypoPhoneticAndTokenHits_AreOnlySuggestedUnderStrict(string query, string suggested)
    {
        string[] names = [suggested, "Bern"];

        var strict = Resolve(names, query, labelWords: GroupLabelWords);
        var lenient = Resolve(names, query, NameMatchPolicy.Lenient, GroupLabelWords);

        strict.Match.ShouldBeNull();
        strict.Suggestion!.Name.ShouldBe(suggested);
        lenient.Match!.Name.ShouldBe(suggested);
    }

    [Test]
    public void EmptyQuery_IsNotFoundUnderBothPolicies()
    {
        var strict = Resolve(["Bern"], "   ");
        var lenient = Resolve(["Bern"], "   ", NameMatchPolicy.Lenient);

        strict.Match.ShouldBeNull();
        strict.Suggestion.ShouldBeNull();
        strict.Candidates.ShouldBeEmpty();
        lenient.Match.ShouldBeNull();
        lenient.Candidates.ShouldBeEmpty();
    }

    [Test]
    public void Variants_OfOneEntityThatOnlyMatchFuzzily_AreSuggestedUnderStrict_NotCollapsedIntoAMatch()
    {
        var vacation = new MultiNamed("vacation", ["Vacances", "Vacance"]);
        var items = new List<MultiNamed> { vacation, new("sick", ["Krankheit", "Maladie"]) };

        var strict = NameResolution.ResolveVariants(items, item => item.Names, "Vacancs");
        var lenient = NameResolution.ResolveVariants(items, item => item.Names, "Vacancs", policy: NameMatchPolicy.Lenient);

        strict.Match.ShouldBeNull();
        strict.Suggestion.ShouldBe(vacation);
        lenient.Match.ShouldBe(vacation);
    }

    [Test]
    public void Variants_ExactVariant_IsMatchedUnderStrict()
    {
        var sick = new MultiNamed("sick", ["Krankheit", "Maladie"]);
        var items = new List<MultiNamed> { new("vacation", ["Vacances", "Ferien"]), sick };

        var result = NameResolution.ResolveVariants(items, item => item.Names, "maladie");

        result.Match.ShouldBe(sick);
    }

    [Test]
    public void SuggestedNameMessage_AsksTheUser_StatesThatNothingChanged_AndCarriesTheAskBackMarker()
    {
        var message = NameResolutionMessages.SuggestedName("Region North");

        message.ShouldBe(
            "Did the user mean 'Region North'? Ask the user to confirm; call again with the exact name only " +
            "after they confirm. Nothing was changed. " + NameResolutionAskBack.Marker);
        NameResolutionAskBack.IsAskBack(message).ShouldBeTrue();
    }

    [Test]
    public void AmbiguousGroupMessage_CarriesTheAskBackMarker()
    {
        var groups = new List<Klacks.Api.Domain.Models.Associations.Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Region North" },
            new() { Id = Guid.NewGuid(), Name = "Region South" }
        };

        var (group, error) = GroupResolver.Resolve(groups, "Region");

        group.ShouldBeNull();
        NameResolutionAskBack.IsAskBack(error).ShouldBeTrue();
    }

    [Test]
    public void NotFoundGroupMessage_DoesNotCarryTheAskBackMarker()
    {
        var groups = new List<Klacks.Api.Domain.Models.Associations.Group> { new() { Id = Guid.NewGuid(), Name = "Bern" } };

        var (_, error) = GroupResolver.Resolve(groups, "Sonderbereich");

        NameResolutionAskBack.IsAskBack(error).ShouldBeFalse();
    }
}
