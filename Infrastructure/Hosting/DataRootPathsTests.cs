using Klacks.Api.Application.Configuration;
using Klacks.Api.Infrastructure.Hosting;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Hosting;

[TestFixture]
public class DataRootPathsTests
{
    private static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "klacks-data-root");
    private static readonly string FallbackBase = Path.Combine(Path.GetTempPath(), "klacks-program");

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Test]
    public void BuildConfigurationOverrides_WithoutDataRoot_ReturnsNothing()
    {
        var configuration = BuildConfiguration(new() { [DataRootPaths.ImagesKey] = "Images" });

        DataRootPaths.BuildConfigurationOverrides(configuration).ShouldBeEmpty();
    }

    [Test]
    public void BuildConfigurationOverrides_WithDataRoot_AnchorsConfiguredRelativePaths()
    {
        var configuration = BuildConfiguration(new()
        {
            [NativeHostingOptions.DataRootKey] = DataRoot,
            [DataRootPaths.ImagesKey] = "Images",
            [DataRootPaths.ErpRootKey] = "ErpImport",
        });

        var overrides = DataRootPaths.BuildConfigurationOverrides(configuration);

        overrides[DataRootPaths.ImagesKey].ShouldBe(Path.Combine(DataRoot, "Images"));
        overrides[DataRootPaths.ErpRootKey].ShouldBe(Path.Combine(DataRoot, "ErpImport"));
    }

    [Test]
    public void BuildConfigurationOverrides_WithDataRoot_AnchorsDefaultsOfUnsetKeys()
    {
        var configuration = BuildConfiguration(new() { [NativeHostingOptions.DataRootKey] = DataRoot });

        var overrides = DataRootPaths.BuildConfigurationOverrides(configuration);

        overrides[DataRootPaths.PayrollRootKey].ShouldBe(Path.Combine(DataRoot, "PayrollExports"));
        overrides[KnowledgeIndexConstants.ModelsRootConfigKey]
            .ShouldBe(Path.Combine(DataRoot, KnowledgeIndexConstants.ModelsCacheSubdirectory));
        overrides[DataRootPaths.DocumentsKey].ShouldBe(Path.Combine(DataRoot, "Documents"));
    }

    [Test]
    public void BuildConfigurationOverrides_WithDataRoot_KeepsAbsolutePaths()
    {
        var explicitImages = Path.Combine(Path.GetTempPath(), "explicit-images");
        var configuration = BuildConfiguration(new()
        {
            [NativeHostingOptions.DataRootKey] = DataRoot,
            [DataRootPaths.ImagesKey] = explicitImages,
        });

        DataRootPaths.BuildConfigurationOverrides(configuration).ShouldNotContainKey(DataRootPaths.ImagesKey);
    }

    [Test]
    public void Resolve_WithoutDataRoot_UsesFallbackBase()
    {
        var configuration = BuildConfiguration(new());

        DataRootPaths.Resolve(configuration, "SeedData", FallbackBase).ShouldBe(Path.Combine(FallbackBase, "SeedData"));
    }

    [Test]
    public void Resolve_WithDataRoot_UsesDataRoot()
    {
        var configuration = BuildConfiguration(new() { [NativeHostingOptions.DataRootKey] = DataRoot });

        DataRootPaths.Resolve(configuration, "SeedData", FallbackBase).ShouldBe(Path.Combine(DataRoot, "SeedData"));
    }

    [Test]
    public void Resolve_AbsolutePath_IsReturnedUnchanged()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "absolute");
        var configuration = BuildConfiguration(new() { [NativeHostingOptions.DataRootKey] = DataRoot });

        DataRootPaths.Resolve(configuration, absolute, FallbackBase).ShouldBe(absolute);
    }

    [Test]
    public void AddedOverrides_WinOverEarlierConfigurationSources()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [NativeHostingOptions.DataRootKey] = DataRoot,
            [DataRootPaths.ImagesKey] = "Images",
        });

        configuration.AddInMemoryCollection(DataRootPaths.BuildConfigurationOverrides(configuration));

        configuration[DataRootPaths.ImagesKey].ShouldBe(Path.Combine(DataRoot, "Images"));
    }
}
