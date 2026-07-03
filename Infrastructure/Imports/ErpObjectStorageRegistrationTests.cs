using Amazon.S3;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Services.Imports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class ErpObjectStorageRegistrationTests
{
    private static ServiceProvider BuildProvider(ErpObjectStorageProvider storageProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ErpObjectStorageOptions { Provider = storageProvider }));
        services.AddSingleton(Substitute.For<IAmazonS3>());
        services.AddErpObjectStorage();
        return services.BuildServiceProvider();
    }

    [Test]
    public void FileSystemProvider_ResolvesFileSystemObjectStorageService()
    {
        using var provider = BuildProvider(ErpObjectStorageProvider.FileSystem);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IObjectStorageService>()
            .ShouldBeOfType<FileSystemObjectStorageService>();
    }

    [Test]
    public void S3Provider_ResolvesS3ObjectStorageService()
    {
        using var provider = BuildProvider(ErpObjectStorageProvider.S3);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IObjectStorageService>()
            .ShouldBeOfType<S3ObjectStorageService>();
    }
}
