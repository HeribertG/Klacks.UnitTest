// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CountryCodeNormalizer: alpha-3 codes normalise to alpha-2, alpha-2 and unknown codes pass
/// through trimmed and unchanged, and blank input is returned as given so callers keep their own
/// "unknown code" handling.
/// </summary>

using Klacks.Api.Application.Constants;

namespace Klacks.UnitTest.Application.Constants;

[TestFixture]
public class CountryCodeNormalizerTests
{
    [TestCase("USA", "US")]
    [TestCase("GBR", "GB")]
    [TestCase("CHE", "CH")]
    [TestCase("DEU", "DE")]
    [TestCase("IND", "IN")]
    [TestCase("ZAF", "ZA")]
    [TestCase("NZL", "NZ")]
    [TestCase("BRA", "BR")]
    [TestCase("usa", "US")]
    [TestCase(" USA ", "US")]
    public void ToAlpha2_Alpha3Code_ReturnsAlpha2(string input, string expected)
    {
        CountryCodeNormalizer.ToAlpha2(input).ShouldBe(expected);
    }

    [TestCase("CH", "CH")]
    [TestCase(" DE ", "DE")]
    [TestCase("ch", "ch")]
    [TestCase("XX", "XX")]
    [TestCase("ZZZ", "ZZZ")]
    [TestCase("Switzerland", "Switzerland")]
    public void ToAlpha2_NotAKnownAlpha3Code_ReturnsTrimmedInput(string input, string expected)
    {
        CountryCodeNormalizer.ToAlpha2(input).ShouldBe(expected);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ToAlpha2_NullOrWhitespace_ReturnsInputUnchanged(string? input)
    {
        CountryCodeNormalizer.ToAlpha2(input).ShouldBe(input);
    }

    [Test]
    public void EveryCountryKnownToCountryTimeZones_IsReachableByItsAlpha3Spelling()
    {
        var reachable = CountryCodeNormalizer.Alpha2CodesReachableFromAlpha3
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unreachable = CountryTimeZones.MappedCountryCodes
            .Concat(CountryTimeZones.DeclaredMultiZoneCountryCodes)
            .Where(code => !reachable.Contains(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        unreachable.ShouldBeEmpty(
            "Every country the application knows must also be reachable by its alpha-3 spelling: the " +
            "seeded country table and deploy/onprem/regions/us.json store the United States as 'USA', " +
            "and a code the normaliser does not know resolves to no time zone at all, which silently " +
            $"leaves the installation on UTC. Missing alpha-3 entries for: {string.Join(", ", unreachable)}");
    }
}
