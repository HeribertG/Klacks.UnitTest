// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard that every country an installation can actually be set up with is one the
/// company clock can resolve a time zone for. Two data sources ship countries: the seeded country
/// table in Infrastructure/Persistence/Seed/DefaultSeed.cs (what the address dropdown offers out of
/// the box) and the on-prem region profiles in deploy/onprem/regions/*.json. A country in either that
/// CountryTimeZones neither maps nor declares multi-zone means an admin who leaves the time-zone
/// setting on its "derive from country" default silently runs the whole installation on UTC - the
/// failure this guard exists to prevent (the United States did exactly that until 2026-09-13, because
/// both sources spell it with the alpha-3 code "USA" while the map is keyed alpha-2).
///
/// A region profile that sets locale.timeZone explicitly is accepted regardless: it does not depend on
/// deriving anything from the country. The seeded table has no such escape, so every country in it must
/// resolve or be declared multi-zone.
///
/// Only the rows between INSERT INTO public.countries and the next INSERT INTO are parsed, never the
/// public.state statement right below it: Swiss canton abbreviations (AR, BE, FR, GR, LU, SG, TI, VS) collide with country codes and
/// would make this guard pass for the wrong reason.
/// </summary>

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class CountryTimeZoneCoverageGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string DefaultSeedRelativePath = "Infrastructure/Persistence/Seed/DefaultSeed.cs";
    private const string RegionProfilesRelativePath = "deploy/onprem/regions";
    private const string CountriesInsertMarker = "INSERT INTO public.countries";
    private const string NextStatementMarker = "INSERT INTO";
    private const string LocaleProperty = "locale";
    private const string CountryProperty = "country";
    private const string TimeZoneProperty = "timeZone";
    private const string JsonFilePattern = "*.json";
    private const int MinimumSeededCountries = 5;
    private const int MinimumRegionProfiles = 20;

    private static readonly Regex CountryRowPattern = new(
        @"\(\s*'[0-9a-fA-F-]{36}'\s*,\s*'(?<code>[A-Za-z]{2,3})'",
        RegexOptions.Compiled);

    [Test]
    public void EverySeededCountry_ResolvesAZoneOrIsDeclaredMultiZone()
    {
        var countryCodes = ReadSeededCountryCodes();

        countryCodes.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumSeededCountries,
            $"Only {countryCodes.Count} seeded countries were parsed out of {DefaultSeedRelativePath}. " +
            "The guard cannot have read the real country table, so a green result would be meaningless.");

        countryCodes.ShouldContain(
            code => string.Equals(code, "USA", StringComparison.OrdinalIgnoreCase),
            "the alpha-3 row is the exact regression this guard exists for - if the parse no longer " +
            "reaches it, the guard is green for the wrong reason");

        var uncovered = countryCodes
            .Where(code => CountryTimeZones.Resolve(code) == null && !CountryTimeZones.IsMultiZoneCountry(code))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        uncovered.ShouldBeEmpty(BuildFailureMessage(uncovered));
    }

    [Test]
    public void EveryRegionProfile_SetsAZoneOrNamesACountryThatResolvesOne()
    {
        var profiles = ReadRegionProfiles();

        profiles.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumRegionProfiles,
            $"Only {profiles.Count} region profiles were read from {RegionProfilesRelativePath}. " +
            "The guard cannot have inspected the real profiles, so a green result would be meaningless.");

        var report = new StringBuilder();
        foreach (var (file, country, timeZone) in profiles)
        {
            if (!string.IsNullOrWhiteSpace(timeZone))
            {
                continue;
            }

            if (CountryTimeZones.Resolve(country) != null || CountryTimeZones.IsMultiZoneCountry(country))
            {
                continue;
            }

            report.AppendLine($"  {file}: country '{country}' resolves to no zone and is not declared " +
                               "multi-zone, and the profile sets no locale.timeZone of its own.");
        }

        report.Length.ShouldBe(
            0,
            "An on-prem region profile must either ship an explicit locale.timeZone or name a country " +
            "the company clock can derive one from. Otherwise the installation runs on UTC without " +
            $"anybody being told.{Environment.NewLine}{report}");
    }

    private static string BuildFailureMessage(IReadOnlyCollection<string> uncovered)
    {
        return "Every country in the seeded country table must resolve to a time zone or be declared a " +
               "multi-zone country in CountryTimeZones. An unknown code makes ICompanyClock fall through " +
               "to UTC, and the admin is never told that the installation computes every business day on " +
               "the wrong boundary. Add the country to the map (single nationwide zone) or to the " +
               $"multi-zone set (so the UI and the setup guidance can say why). Uncovered: {string.Join(", ", uncovered)}";
    }

    private static List<string> ReadSeededCountryCodes()
    {
        var seedFile = Path.Combine(LocateApiProject(), DefaultSeedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var content = File.ReadAllText(seedFile);

        var start = content.IndexOf(CountriesInsertMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"'{CountriesInsertMarker}' was not found in {DefaultSeedRelativePath}.");

        var searchFrom = start + CountriesInsertMarker.Length;
        var end = content.IndexOf(NextStatementMarker, searchFrom, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(
            start,
            $"No statement follows {CountriesInsertMarker}, so the country rows could not be delimited " +
            "from the public.state rows below them - whose Swiss canton abbreviations would make this " +
            "guard pass for the wrong reason.");

        var statement = content[start..end];

        return CountryRowPattern.Matches(statement)
            .Select(match => match.Groups["code"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<(string File, string? Country, string? TimeZone)> ReadRegionProfiles()
    {
        var directory = Path.Combine(LocateApiProject(), RegionProfilesRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.Exists(directory).ShouldBeTrue($"{RegionProfilesRelativePath} was not found under the Klacks.Api project.");

        var profiles = new List<(string File, string? Country, string? TimeZone)>();
        foreach (var file in Directory.EnumerateFiles(directory, JsonFilePattern))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty(LocaleProperty, out var locale))
            {
                continue;
            }

            var country = locale.TryGetProperty(CountryProperty, out var countryElement) ? countryElement.GetString() : null;
            var timeZone = locale.TryGetProperty(TimeZoneProperty, out var timeZoneElement) ? timeZoneElement.GetString() : null;
            profiles.Add((Path.GetFileName(file), country, timeZone));
        }

        return profiles;
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, "Domain", "Services")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
