// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for SearchGeocodingQueryValidator: query 2 to 200 characters after trimming, optional BCP 47 language of at
/// most 35 characters.
/// </summary>

using Klacks.Api.Application.Queries.Geocoding;
using Klacks.Api.Application.Validation.Geocoding;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Validation.Geocoding;

[TestFixture]
public class SearchGeocodingQueryValidatorTests
{
    private SearchGeocodingQueryValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new SearchGeocodingQueryValidator();
    }

    [TestCase("Zürich", null)]
    [TestCase("Bahnhofstrasse 1, 8001 Zürich", "de")]
    [TestCase("ab", "de-CH")]
    [TestCase("Tokyo", "zh-Hant-TW")]
    [TestCase("Paris", "")]
    public void Validate_ValidInput_Passes(string query, string? language)
    {
        _validator.Validate(new SearchGeocodingQuery(query, language)).IsValid.ShouldBeTrue();
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("a")]
    [TestCase(" a ")]
    public void Validate_TooShortQuery_Fails(string query)
    {
        var result = _validator.Validate(new SearchGeocodingQuery(query, null));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(SearchGeocodingQuery.Query));
    }

    [Test]
    public void Validate_QueryAtMaximumLength_Passes()
    {
        var query = new string('x', SearchGeocodingQueryValidator.MaximumQueryLength);

        _validator.Validate(new SearchGeocodingQuery(query, null)).IsValid.ShouldBeTrue();
    }

    [Test]
    public void Validate_QueryAboveMaximumLength_Fails()
    {
        var query = new string('x', SearchGeocodingQueryValidator.MaximumQueryLength + 1);

        _validator.Validate(new SearchGeocodingQuery(query, null)).IsValid.ShouldBeFalse();
    }

    [TestCase("de,en")]
    [TestCase("d")]
    [TestCase("de_CH")]
    [TestCase("de;q=0.9")]
    [TestCase("<script>")]
    public void Validate_InvalidLanguage_Fails(string language)
    {
        var result = _validator.Validate(new SearchGeocodingQuery("Zürich", language));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldAllBe(e => e.PropertyName == nameof(SearchGeocodingQuery.Language));
    }

    [Test]
    public void Validate_LanguageAboveMaximumLength_Fails()
    {
        var language = "de-" + string.Join("-", Enumerable.Repeat("abcdefgh", 4));
        language.Length.ShouldBeGreaterThan(SearchGeocodingQueryValidator.MaximumLanguageLength);

        _validator.Validate(new SearchGeocodingQuery("Zürich", language)).IsValid.ShouldBeFalse();
    }
}
