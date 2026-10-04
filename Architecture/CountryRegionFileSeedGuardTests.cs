// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Services.Geo;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class CountryRegionFileSeedGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SwissCountryCode = "CH";

    private static readonly IReadOnlyDictionary<string, string> DemoSeedRegionByCanton = new Dictionary<string, string>
    {
        ["GE"] = "Westschweiz", ["VD"] = "Westschweiz", ["NE"] = "Westschweiz", ["JU"] = "Westschweiz", ["FR"] = "Westschweiz",
        ["ZH"] = "Deutschschweiz Zürich", ["AG"] = "Deutschschweiz Zürich",
        ["BE"] = "Deutschschweiz Mitte", ["SO"] = "Deutschschweiz Mitte", ["BS"] = "Deutschschweiz Mitte", ["BL"] = "Deutschschweiz Mitte",
        ["LU"] = "Deutschschweiz Ost", ["SG"] = "Deutschschweiz Ost", ["TG"] = "Deutschschweiz Ost", ["AI"] = "Deutschschweiz Ost",
        ["AR"] = "Deutschschweiz Ost", ["GL"] = "Deutschschweiz Ost", ["GR"] = "Deutschschweiz Ost", ["NW"] = "Deutschschweiz Ost",
        ["OW"] = "Deutschschweiz Ost", ["SH"] = "Deutschschweiz Ost", ["SZ"] = "Deutschschweiz Ost", ["TI"] = "Deutschschweiz Ost",
        ["UR"] = "Deutschschweiz Ost", ["VS"] = "Deutschschweiz Ost", ["ZG"] = "Deutschschweiz Ost"
    };

    [Test]
    public async Task ShippedSwissRegionFile_MatchesTheDocumentedRegionConvention()
    {
        var provider = new FileCountryRegionProvider(Path.Combine(FindRepositoryRoot(), ApiProjectDirectory, CountryRegionFileLayout.Directory));

        var map = await provider.GetRegionByStateAsync(SwissCountryCode);

        map.Count.ShouldBe(DemoSeedRegionByCanton.Count);
        foreach (var (canton, region) in DemoSeedRegionByCanton)
        {
            map.ShouldContainKey(canton);
            map[canton].ShouldBe(region, $"canton {canton}");
        }
    }

    private static string FindRepositoryRoot()
    {
        return RepositoryRootLocator.RequireRoot();
    }
}
