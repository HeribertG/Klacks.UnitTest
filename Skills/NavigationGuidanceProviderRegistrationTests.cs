// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the DI wiring of INavigationGuidanceProvider: an unregistered provider would fail
/// silently — NavigateToSkill would receive an empty provider list and the guidance note
/// would simply never appear, without any build or runtime error.
/// </summary>

using Klacks.Api.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class NavigationGuidanceProviderRegistrationTests
{
    [Test]
    public void EveryNavigationGuidanceProvider_IsRegisteredInLLMCoreServices()
    {
        var services = new ServiceCollection();
        services.AddLLMCoreServices();

        var providerTypes = typeof(INavigationGuidanceProvider).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(INavigationGuidanceProvider).IsAssignableFrom(t))
            .ToList();

        Assert.That(providerTypes, Is.Not.Empty);
        foreach (var providerType in providerTypes)
        {
            var registered = services.Any(d =>
                d.ServiceType == typeof(INavigationGuidanceProvider) &&
                d.ImplementationType == providerType);
            Assert.That(registered, Is.True,
                $"{providerType.Name} implements INavigationGuidanceProvider but is not registered in " +
                "AddLLMCoreServices — NavigateToSkill would silently receive no guidance from it.");
        }
    }
}
