// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupSemanticsClassifier — one test per category, a multi-match case (a group
/// name that is both a canton and a city name), the Other fallback, and case/whitespace tolerance.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupSemanticsClassifierTests
{
    private static GroupSemanticsReferenceData ReferenceData(
        IEnumerable<string>? cantonTokens = null,
        IEnumerable<string>? cityNames = null,
        IEnumerable<string>? qualificationNames = null,
        IEnumerable<string>? contractNames = null)
    {
        return new GroupSemanticsReferenceData(
            (cantonTokens ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (cityNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (qualificationNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
            (contractNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    [Test]
    public void Classify_MatchesCanton_ByAbbreviationOrName()
    {
        var referenceData = ReferenceData(cantonTokens: ["BE", "Bern"]);

        GroupSemanticsClassifier.Classify("BE", false, referenceData)
            .ShouldBe([GroupSemanticCategory.Canton], ignoreOrder: true);
        GroupSemanticsClassifier.Classify("Bern", false, referenceData)
            .ShouldBe([GroupSemanticCategory.Canton], ignoreOrder: true);
    }

    [Test]
    public void Classify_MatchesCity()
    {
        var referenceData = ReferenceData(cityNames: ["Winterthur"]);

        GroupSemanticsClassifier.Classify("Winterthur", false, referenceData)
            .ShouldBe([GroupSemanticCategory.City], ignoreOrder: true);
    }

    [Test]
    public void Classify_MatchesQualification()
    {
        var referenceData = ReferenceData(qualificationNames: ["Staplerschein"]);

        GroupSemanticsClassifier.Classify("Staplerschein", false, referenceData)
            .ShouldBe([GroupSemanticCategory.Qualification], ignoreOrder: true);
    }

    [Test]
    public void Classify_MatchesContract()
    {
        var referenceData = ReferenceData(contractNames: ["180 BE"]);

        GroupSemanticsClassifier.Classify("180 BE", false, referenceData)
            .ShouldBe([GroupSemanticCategory.Contract], ignoreOrder: true);
    }

    [Test]
    public void Classify_MatchesGeo_WhenCoordinatesArePresent()
    {
        var referenceData = ReferenceData();

        GroupSemanticsClassifier.Classify("Aussendienst Ost", true, referenceData)
            .ShouldBe([GroupSemanticCategory.Geo], ignoreOrder: true);
    }

    [Test]
    public void Classify_ReturnsMultipleCategories_WhenNameMatchesSeveral()
    {
        var referenceData = ReferenceData(cantonTokens: ["Zürich"], cityNames: ["Zürich"]);

        GroupSemanticsClassifier.Classify("Zürich", true, referenceData)
            .ShouldBe(
                [GroupSemanticCategory.Canton, GroupSemanticCategory.City, GroupSemanticCategory.Geo],
                ignoreOrder: true);
    }

    [Test]
    public void Classify_FallsBackToOther_WhenNothingMatchesAndNoCoordinates()
    {
        var referenceData = ReferenceData(
            cantonTokens: ["BE"], cityNames: ["Bern"], qualificationNames: ["Erste Hilfe"], contractNames: ["180 BE"]);

        GroupSemanticsClassifier.Classify("Administration", false, referenceData)
            .ShouldBe([GroupSemanticCategory.Other], ignoreOrder: true);
    }

    [Test]
    public void Classify_IsCaseInsensitive_AndTrimsWhitespace()
    {
        var referenceData = ReferenceData(cityNames: ["Uster"]);

        GroupSemanticsClassifier.Classify("  uster  ", false, referenceData)
            .ShouldBe([GroupSemanticCategory.City], ignoreOrder: true);
    }

    [Test]
    public void Classify_ReturnsOther_ForNullOrEmptyName_WithoutCoordinates()
    {
        var referenceData = ReferenceData(cityNames: ["Uster"]);

        GroupSemanticsClassifier.Classify(null, false, referenceData)
            .ShouldBe([GroupSemanticCategory.Other], ignoreOrder: true);
    }
}
