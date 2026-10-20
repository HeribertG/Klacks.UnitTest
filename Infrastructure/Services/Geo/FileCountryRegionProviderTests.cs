// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Infrastructure.Services.Geo;

namespace Klacks.UnitTest.Infrastructure.Services.Geo;

[TestFixture]
public class FileCountryRegionProviderTests
{
    private const string WestNames = """{ "de": "Westschweiz", "en": "Western Switzerland", "fr": "Suisse romande", "it": "Svizzera romanda" }""";
    private const string EastNames = """{ "de": "Ost", "en": "East", "fr": "Est", "it": "Est" }""";

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "klacks-regions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task GetRegionByStateAsync_FileExists_MapsEveryStateCodeToItsRegionWithKeyAndNames()
    {
        Write("CH", $$"""{ "regions": [ { "key": "west", "names": {{WestNames}}, "states": ["GE", "vd"] }, { "key": "ost", "names": {{EastNames}}, "states": ["SG"] } ] }""");
        var provider = new FileCountryRegionProvider(_root);

        var map = await provider.GetRegionByStateAsync("ch");

        map.Count.ShouldBe(3);
        map["GE"].Key.ShouldBe("west");
        map["VD"].Names.GetValue("fr").ShouldBe("Suisse romande");
        map["sg"].Key.ShouldBe("ost");
        map["sg"].Names.GetValue("de").ShouldBe("Ost");
    }

    [Test]
    public async Task GetRegionByStateAsync_AlphaThreeCountryCode_ReadsTheAlphaTwoFile()
    {
        Write("US", $$"""{ "regions": [ { "key": "south", "names": {{EastNames}}, "states": ["DC"] } ] }""");
        var provider = new FileCountryRegionProvider(_root);

        var map = await provider.GetRegionByStateAsync("USA");

        map["DC"].Key.ShouldBe("south");
    }

    [Test]
    public async Task GetRegionByStateAsync_NoFile_ReturnsEmptyMap()
    {
        var provider = new FileCountryRegionProvider(_root);

        var map = await provider.GetRegionByStateAsync("DE");

        map.ShouldBeEmpty();
    }

    [Test]
    public async Task GetRegionByStateAsync_EmptyCountryCode_ReturnsEmptyMap()
    {
        var provider = new FileCountryRegionProvider(_root);

        var map = await provider.GetRegionByStateAsync("  ");

        map.ShouldBeEmpty();
    }

    [Test]
    public async Task GetRegionByStateAsync_MalformedFile_ThrowsNamingTheFile()
    {
        Write("AT", "{ not json");

        await ShouldFailNamingTheFile("AT");
    }

    [Test]
    public async Task GetRegionByStateAsync_NullDocument_ThrowsNamingTheFile()
    {
        Write("AT", "null");

        await ShouldFailNamingTheFile("AT");
    }

    [Test]
    public async Task GetRegionByStateAsync_RegionWithoutKey_ThrowsNamingTheFile()
    {
        Write("AT", $$"""{ "regions": [ { "names": {{WestNames}}, "states": ["GE"] } ] }""");

        await ShouldFailNamingTheFile("AT", "without a key");
    }

    [Test]
    public async Task GetRegionByStateAsync_OldNameOnlyFormat_IsRejected()
    {
        Write("AT", """{ "regions": [ { "name": "Westschweiz", "states": ["GE"] } ] }""");

        await ShouldFailNamingTheFile("AT", "without a key");
    }

    [Test]
    public async Task GetRegionByStateAsync_MissingCoreLanguage_ThrowsNamingTheFileAndLanguage()
    {
        Write("AT", """{ "regions": [ { "key": "west", "names": { "de": "Westschweiz", "en": "West", "fr": "Ouest" }, "states": ["GE"] } ] }""");

        await ShouldFailNamingTheFile("AT", "'it'");
    }

    [Test]
    public async Task GetRegionByStateAsync_DuplicateKey_ThrowsNamingTheFile()
    {
        Write("AT", $$"""{ "regions": [ { "key": "west", "names": {{WestNames}}, "states": ["GE"] }, { "key": "WEST", "names": {{EastNames}}, "states": ["SG"] } ] }""");

        await ShouldFailNamingTheFile("AT", "more than once");
    }

    [Test]
    public async Task GetRegionByStateAsync_StateInTwoRegions_ThrowsNamingTheFile()
    {
        Write("AT", $$"""{ "regions": [ { "key": "west", "names": {{WestNames}}, "states": ["GE"] }, { "key": "ost", "names": {{EastNames}}, "states": ["ge"] } ] }""");

        await ShouldFailNamingTheFile("AT", "state 'GE'");
    }

    [Test]
    public async Task GetRegionByStateAsync_RegionWithoutStates_ThrowsNamingTheFile()
    {
        Write("AT", $$"""{ "regions": [ { "key": "west", "names": {{WestNames}}, "states": [] } ] }""");

        await ShouldFailNamingTheFile("AT", "lists no states");
    }

    [Test]
    public async Task GetRegionByStateAsync_SecondCall_ServesFromCache()
    {
        Write("CH", $$"""{ "regions": [ { "key": "west", "names": {{WestNames}}, "states": ["GE"] } ] }""");
        var provider = new FileCountryRegionProvider(_root);
        await provider.GetRegionByStateAsync("CH");

        File.Delete(Path.Combine(_root, "CH.json"));
        var map = await provider.GetRegionByStateAsync("CH");

        map["GE"].Key.ShouldBe("west");
    }

    private void Write(string country, string content) =>
        File.WriteAllText(Path.Combine(_root, country + ".json"), content);

    private async Task ShouldFailNamingTheFile(string country, string? detail = null)
    {
        var provider = new FileCountryRegionProvider(_root);

        var ex = await Should.ThrowAsync<InvalidDataException>(() => provider.GetRegionByStateAsync(country));

        ex.Message.ShouldContain(country + ".json");
        if (detail != null)
        {
            ex.Message.ShouldContain(detail);
        }
    }
}
