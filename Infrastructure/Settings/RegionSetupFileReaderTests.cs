// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Services.Settings;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class RegionSetupFileReaderTests
{
    private List<string> _tempFiles = null!;

    [SetUp]
    public void SetUp()
    {
        _tempFiles = new List<string>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    [Test]
    public void GetConfiguredPath_NoValueConfigured_ReturnsNull()
    {
        var configuration = BuildConfiguration(null);

        RegionSetupFileReader.GetConfiguredPath(configuration).ShouldBeNull();
    }

    [Test]
    public void GetConfiguredPath_WhitespaceValue_ReturnsNull()
    {
        var configuration = BuildConfiguration("   ");

        RegionSetupFileReader.GetConfiguredPath(configuration).ShouldBeNull();
    }

    [Test]
    public void GetConfiguredPath_ValueConfigured_ReturnsPath()
    {
        var configuration = BuildConfiguration("/app/setup/region-setup.json");

        RegionSetupFileReader.GetConfiguredPath(configuration).ShouldBe("/app/setup/region-setup.json");
    }

    [Test]
    public async Task ReadContentAsync_FileMissing_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");

        await Should.ThrowAsync<FileNotFoundException>(() => RegionSetupFileReader.ReadContentAsync(missingPath));
    }

    [Test]
    public async Task ReadProfileAsync_FileMissing_ThrowsFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");

        await Should.ThrowAsync<FileNotFoundException>(() => RegionSetupFileReader.ReadProfileAsync(missingPath));
    }

    [Test]
    public async Task ReadProfileAsync_InvalidJson_ThrowsInvalidRequest()
    {
        var path = WriteTempFile("{ this is not json");

        await Should.ThrowAsync<InvalidRequestException>(() => RegionSetupFileReader.ReadProfileAsync(path));
    }

    [Test]
    public async Task ReadProfileAsync_UnknownProperty_ThrowsInvalidRequest()
    {
        var path = WriteTempFile("""{ "version": 1, "regionTypo": "DE" }""");

        await Should.ThrowAsync<InvalidRequestException>(() => RegionSetupFileReader.ReadProfileAsync(path));
    }

    [Test]
    public async Task ReadProfileAsync_VersionMissing_ThrowsInvalidRequest()
    {
        var path = WriteTempFile("""{ "region": "DE" }""");

        await Should.ThrowAsync<InvalidRequestException>(() => RegionSetupFileReader.ReadProfileAsync(path));
    }

    [Test]
    public async Task ReadProfileAsync_VersionUnsupported_ThrowsInvalidRequest()
    {
        var path = WriteTempFile("""{ "version": 2, "region": "DE" }""");

        await Should.ThrowAsync<InvalidRequestException>(() => RegionSetupFileReader.ReadProfileAsync(path));
    }

    [Test]
    public async Task ReadProfileAsync_NullJsonLiteral_ThrowsInvalidRequest()
    {
        var path = WriteTempFile("null");

        await Should.ThrowAsync<InvalidRequestException>(() => RegionSetupFileReader.ReadProfileAsync(path));
    }

    [Test]
    public async Task ReadProfileAsync_HappyPath_ParsesProfileIncludingSeedDemoData()
    {
        var json = """
            {
              "version": 1,
              "region": "DE",
              "locale": { "country": "DE", "state": "BY" },
              "seedDemoData": true
            }
            """;
        var path = WriteTempFile(json);

        var profile = await RegionSetupFileReader.ReadProfileAsync(path);

        profile.Version.ShouldBe(1);
        profile.Region.ShouldBe("DE");
        profile.Locale.ShouldNotBeNull();
        profile.Locale!.Country.ShouldBe("DE");
        profile.Locale.State.ShouldBe("BY");
        profile.SeedDemoData.ShouldBe(true);
    }

    [Test]
    public async Task ReadProfileAsync_SeedDemoDataOmitted_IsNull()
    {
        var path = WriteTempFile("""{ "version": 1, "region": "DE" }""");

        var profile = await RegionSetupFileReader.ReadProfileAsync(path);

        profile.SeedDemoData.ShouldBeNull();
    }

    private static IConfiguration BuildConfiguration(string? filePath)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [RegionSetupFileReader.FileConfigKey] = filePath
            })
            .Build();
    }

    private string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"region-setup-{Guid.NewGuid()}.json");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }
}
