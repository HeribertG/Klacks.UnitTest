// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the shipped country region files (GeoData/Regions/{CC}.json): every file loads through the
/// provider (keys present and unique, core languages present, no state twice), names every region in every
/// language Klacks ships (core plus every language pack) including the country's official language, covers
/// exactly the state codes the installation knows for that country (seeded states for CH/FR/US, the ja pack's
/// states for JP), never gives two different regions the same name in any language (an existing root group is
/// reused when it carries ANY name of a region, so a shared name would hand one region's root to another), and
/// the Swiss file keeps the German names and canton split of the demo group seed.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Data.Seed.Demo;
using Klacks.Api.Domain.Common;
using Klacks.Api.Infrastructure.Services.Geo;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class CountryRegionFileSeedGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SwissCountryCode = "CH";
    private const string GermanLanguage = "de";
    private const string LanguagePacksDirectory = "Plugins/Languages";
    private const string DefaultSeedPath = "Infrastructure/Persistence/Seed/DefaultSeed.cs";
    private const string JapaneseStatesPath = "Plugins/Languages/ja/states.json";

    private static readonly Regex SeededStateRow = new(
        @"\('[0-9a-f-]{36}', '(?<code>[^']+)', '(?<prefix>[A-Z]+)', '\{",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private static readonly IReadOnlyDictionary<string, string> OfficialLanguageByCountry = new Dictionary<string, string>
    {
        ["CH"] = "de",
        ["FR"] = "fr",
        ["JP"] = "ja",
        ["US"] = "en"
    };

    private static readonly IReadOnlyDictionary<string, string> SeedCountryPrefixByCountry = new Dictionary<string, string>
    {
        ["CH"] = "CH",
        ["FR"] = "FR",
        ["US"] = "USA"
    };

    private static readonly IReadOnlyDictionary<string, string> DemoSeedRegionByCanton = new Dictionary<string, string>
    {
        ["GE"] = "Westschweiz", ["VD"] = "Westschweiz", ["NE"] = "Westschweiz", ["JU"] = "Westschweiz", ["FR"] = "Westschweiz",
        ["ZH"] = "Deutschschweiz Zürich", ["AG"] = "Deutschschweiz Zürich",
        ["BE"] = "Deutschschweiz Mitte", ["SO"] = "Deutschschweiz Mitte", ["BS"] = "Deutschschweiz Mitte", ["BL"] = "Deutschschweiz Mitte",
        ["LU"] = "Deutschschweiz Ost", ["SG"] = "Deutschschweiz Ost", ["TG"] = "Deutschschweiz Ost", ["AI"] = "Deutschschweiz Ost",
        ["AR"] = "Deutschschweiz Ost", ["GL"] = "Deutschschweiz Ost", ["GR"] = "Deutschschweiz Ost", ["NW"] = "Deutschschweiz Ost",
        ["OW"] = "Deutschschweiz Ost", ["SH"] = "Deutschschweiz Ost", ["SZ"] = "Deutschschweiz Ost", ["UR"] = "Deutschschweiz Ost",
        ["VS"] = "Deutschschweiz Ost", ["ZG"] = "Deutschschweiz Ost",
        ["TI"] = "Italienische Schweiz"
    };

    [Test]
    public void EveryShippedRegionFile_HasAKnownOfficialLanguage()
    {
        ShippedCountries().ShouldNotBeEmpty();
        foreach (var country in ShippedCountries())
        {
            OfficialLanguageByCountry.ShouldContainKey(country, $"add the official language of {country} to this guard");
        }
    }

    [Test]
    public async Task EveryShippedRegionFile_NamesEveryRegionInEveryShippedLanguage()
    {
        var languages = ShippedLanguages();
        var failures = new List<string>();

        foreach (var country in ShippedCountries())
        {
            foreach (var region in (await Provider().GetRegionByStateAsync(country)).Values.DistinctBy(r => r.Key))
            {
                var missing = languages
                    .Append(OfficialLanguageByCountry[country])
                    .Where(language => string.IsNullOrWhiteSpace(region.Names.GetValue(language)))
                    .Distinct()
                    .ToList();
                if (missing.Count > 0)
                {
                    failures.Add($"{country}/{region.Key}: missing {string.Join(", ", missing)}");
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Test]
    public async Task EveryShippedRegionFile_CoversExactlyTheKnownStatesOfItsCountry()
    {
        foreach (var country in ShippedCountries())
        {
            var map = await Provider().GetRegionByStateAsync(country);
            var known = KnownStateCodes(country);

            map.Keys.Select(k => k.ToUpperInvariant()).OrderBy(k => k, StringComparer.Ordinal)
                .ShouldBe(known.OrderBy(k => k, StringComparer.Ordinal), $"{country}: region file vs. known state codes");
        }
    }

    [Test]
    public async Task NoTwoRegions_ShareAName_InAnyLanguage_AcrossAllShippedFiles()
    {
        var ownerByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var country in ShippedCountries())
        {
            foreach (var region in (await Provider().GetRegionByStateAsync(country)).Values.DistinctBy(r => r.Key))
            {
                var owner = country + "/" + region.Key;
                foreach (var name in region.Names.ToDictionary().Values.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (ownerByName.TryGetValue(name, out var other) && other != owner)
                    {
                        failures.Add($"'{name}' is used by {other} and {owner}");
                    }

                    ownerByName.TryAdd(name, owner);
                }
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Test]
    public async Task ShippedSwissRegionFile_MatchesTheDemoGroupSeed()
    {
        var map = await Provider().GetRegionByStateAsync(SwissCountryCode);

        map.Count.ShouldBe(DemoSeedRegionByCanton.Count);
        foreach (var (canton, region) in DemoSeedRegionByCanton)
        {
            map.ShouldContainKey(canton);
            map[canton].Names.GetValue(GermanLanguage).ShouldBe(region, $"canton {canton}");
        }

        map.Values.Select(r => r.Names.GetValue(GermanLanguage)).Distinct().OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(DemoOrderDefinitionFactory.RootGroups.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static FileCountryRegionProvider Provider() =>
        new(Path.Combine(ApiRoot(), CountryRegionFileLayout.Directory));

    private static IReadOnlyList<string> ShippedCountries() =>
        Directory.GetFiles(Path.Combine(ApiRoot(), CountryRegionFileLayout.Directory), "*" + CountryRegionFileLayout.FileExtension)
            .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> ShippedLanguages() =>
        MultiLanguage.CoreLanguages
            .Concat(Directory.GetDirectories(Path.Combine(ApiRoot(), LanguagePacksDirectory))
                .Select(dir => Path.GetFileName(dir).ToLowerInvariant()))
            .Distinct()
            .ToList();

    private static IReadOnlyList<string> KnownStateCodes(string country)
    {
        if (SeedCountryPrefixByCountry.TryGetValue(country, out var prefix))
        {
            var source = File.ReadAllText(Path.Combine(ApiRoot(), DefaultSeedPath));
            return SeededStateRow.Matches(source)
                .Where(m => m.Groups["prefix"].Value == prefix)
                .Select(m => m.Groups["code"].Value.ToUpperInvariant())
                .ToList();
        }

        country.ShouldBe("JP", $"no state source known for {country}; extend this guard");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ApiRoot(), JapaneseStatesPath)));
        return document.RootElement.EnumerateArray()
            .Where(state => state.GetProperty("countryPrefix").GetString() == country)
            .Select(state => state.GetProperty("abbreviation").GetString()!.ToUpperInvariant())
            .ToList();
    }

    private static string ApiRoot() => Path.Combine(RepositoryRootLocator.RequireRoot(), ApiProjectDirectory);
}
