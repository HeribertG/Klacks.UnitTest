// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Reflection;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class LLMProviderModelTestCoverageTests
{
    private const string TestModelMethodName = nameof(ILLMProvider.TestModelAsync);

    [Test]
    public void EveryChatProvider_MustImplementTestModelAsync()
    {
        var offenders = ConcreteProviderTypes()
            .Where(UsesDefaultInterfaceImplementation)
            .Select(t => t.Name)
            .OrderBy(name => name)
            .ToList();

        Assert.That(offenders, Is.Empty,
            $"These providers fall back to the ILLMProvider.{TestModelMethodName} stub, which always reports " +
            "'Provider does not support testing'. The model sync treats that as a failed test and soft-deletes " +
            $"every discovered model. Implement {TestModelMethodName} or inherit from a base provider that does: " +
            string.Join(", ", offenders));
    }

    private static IEnumerable<Type> ConcreteProviderTypes() =>
        typeof(ILLMProvider).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ILLMProvider).IsAssignableFrom(t));

    private static bool UsesDefaultInterfaceImplementation(Type providerType)
    {
        var map = providerType.GetInterfaceMap(typeof(ILLMProvider));

        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i].Name != TestModelMethodName)
            {
                continue;
            }

            return map.TargetMethods[i].DeclaringType == typeof(ILLMProvider);
        }

        return false;
    }
}
