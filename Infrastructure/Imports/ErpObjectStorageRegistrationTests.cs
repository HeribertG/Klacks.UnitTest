using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Services.Imports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class ErpObjectStorageRegistrationTests
{
    [Test]
    public void AddErpObjectStorage_ResolvesFileSystemObjectStorageService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(new ErpObjectStorageOptions()));
        services.AddErpObjectStorage();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IObjectStorageService>()
            .ShouldBeOfType<FileSystemObjectStorageService>();
    }
}
