using System.Runtime.InteropServices;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Models.Update;
using Klacks.Api.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Hosting;

[TestFixture]
public class InstallationIdentityProviderTests
{
    [Test]
    public void WithoutDataRoot_IsDocker()
    {
        var provider = new InstallationIdentityProvider(new ConfigurationBuilder().Build());

        provider.Get().ShouldBe(new InstallationIdentity(InstallationKind.Docker, RuntimeInformation.RuntimeIdentifier));
    }

    [Test]
    public void WithDataRoot_IsNative()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [NativeHostingOptions.DataRootKey] = Path.Combine(Path.GetTempPath(), "klacks-data"),
            })
            .Build();

        new InstallationIdentityProvider(configuration).Get().Kind.ShouldBe(InstallationKind.Native);
    }
}
