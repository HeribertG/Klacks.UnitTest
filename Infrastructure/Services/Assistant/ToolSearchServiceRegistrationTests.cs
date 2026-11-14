// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The in-turn tool search hands its finds from the find_tools skill to the function executor through
/// ITurnToolsetExtension, so the extension must be scoped: both are scoped, and a transient extension would give each
/// its own instance and silently drop every find. The switch is read from Assistant:ToolSearch:Enabled and is off
/// when the key is missing.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.ToolSearch;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class ToolSearchServiceRegistrationTests
{
    private IServiceCollection _services = null!;

    [SetUp]
    public void SetUp()
    {
        _services = new ServiceCollection();
        _services.AddLLMCoreServices();
    }

    [Test]
    public void TheTurnToolsetExtension_IsScoped()
    {
        Descriptor<ITurnToolsetExtension>().ImplementationType.ShouldBe(typeof(TurnToolsetExtension));
        Descriptor<ITurnToolsetExtension>().Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Test]
    public void TheToolSearchAppender_IsScoped()
    {
        Descriptor<IToolSearchToolsetAppender>().ImplementationType.ShouldBe(typeof(ToolSearchToolsetAppender));
        Descriptor<IToolSearchToolsetAppender>().Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Test]
    public void OneScope_SharesOneExtensionInstance()
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(Descriptor<ITurnToolsetExtension>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITurnToolsetExtension>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<ITurnToolsetExtension>());
    }

    [TestCase(null, false)]
    [TestCase("false", false)]
    [TestCase("true", true)]
    public void TheSwitch_IsReadFromConfiguration_AndOffByDefault(string? configured, bool expected)
    {
        var settings = new Dictionary<string, string?>();
        if (configured != null)
        {
            settings[ToolSearchOptions.EnabledConfigKey] = configured;
        }

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.Add(Descriptor<ToolSearchOptions>());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ToolSearchOptions>().Enabled.ShouldBe(expected);
    }

    private ServiceDescriptor Descriptor<TService>() => _services.Single(d => d.ServiceType == typeof(TService));
}
