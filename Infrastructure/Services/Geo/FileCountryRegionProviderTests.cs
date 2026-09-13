// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Infrastructure.Services.Geo;

namespace Klacks.UnitTest.Infrastructure.Services.Geo;

[TestFixture]
public class FileCountryRegionProviderTests
{
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
    public async Task GetRegionByStateAsync_FileExists_MapsEveryStateCodeToItsRegion()
    {
        File.WriteAllText(Path.Combine(_root, "CH.json"),
            """{ "regions": [ { "name": "Westschweiz", "states": ["GE", "vd"] }, { "name": "Ost", "states": ["SG"] } ] }""");
        var provider = new FileCountryRegionProvider(_root);

        var map = await provider.GetRegionByStateAsync("ch");

        map.Count.ShouldBe(3);
        map["GE"].ShouldBe("Westschweiz");
        map["VD"].ShouldBe("Westschweiz");
        map["sg"].ShouldBe("Ost");
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
        File.WriteAllText(Path.Combine(_root, "AT.json"), "{ not json");
        var provider = new FileCountryRegionProvider(_root);

        var ex = await Should.ThrowAsync<InvalidDataException>(() => provider.GetRegionByStateAsync("AT"));

        ex.Message.ShouldContain("AT.json");
    }

    [Test]
    public async Task GetRegionByStateAsync_NullDocument_ThrowsNamingTheFile()
    {
        File.WriteAllText(Path.Combine(_root, "AT.json"), "null");
        var provider = new FileCountryRegionProvider(_root);

        var ex = await Should.ThrowAsync<InvalidDataException>(() => provider.GetRegionByStateAsync("AT"));

        ex.Message.ShouldContain("AT.json");
    }

    [Test]
    public async Task GetRegionByStateAsync_RegionWithoutName_ThrowsNamingTheFile()
    {
        File.WriteAllText(Path.Combine(_root, "AT.json"), """{ "regions": [ { "states": ["GE"] } ] }""");
        var provider = new FileCountryRegionProvider(_root);

        var ex = await Should.ThrowAsync<InvalidDataException>(() => provider.GetRegionByStateAsync("AT"));

        ex.Message.ShouldContain("AT.json");
    }

    [Test]
    public async Task GetRegionByStateAsync_SecondCall_ServesFromCache()
    {
        var path = Path.Combine(_root, "CH.json");
        File.WriteAllText(path, """{ "regions": [ { "name": "Westschweiz", "states": ["GE"] } ] }""");
        var provider = new FileCountryRegionProvider(_root);
        await provider.GetRegionByStateAsync("CH");

        File.Delete(path);
        var map = await provider.GetRegionByStateAsync("CH");

        map["GE"].ShouldBe("Westschweiz");
    }
}
