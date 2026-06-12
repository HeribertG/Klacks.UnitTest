// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Collections;
using System.Reflection;
using Klacks.Api.Application.Constants;
using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpRateLimitingTests
{
    private const string PolicyMapPropertyName = "PolicyMap";

    [Test]
    public void AddKlacksMcpServer_ResolvesRateLimiterOptions()
    {
        var services = new ServiceCollection();

        services.AddKlacksMcpServer();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.That(options, Is.Not.Null);
    }

    [Test]
    public void AddKlacksMcpServer_RegistersMcpRateLimiterPolicy()
    {
        var services = new ServiceCollection();

        services.AddKlacksMcpServer();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.That(GetRegisteredPolicyNames(options), Does.Contain(RateLimitingPolicies.Mcp));
    }

    private static IReadOnlyCollection<string> GetRegisteredPolicyNames(RateLimiterOptions options)
    {
        var policyMapProperty = typeof(RateLimiterOptions).GetProperty(
            PolicyMapPropertyName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(policyMapProperty, Is.Not.Null);

        var policyMap = (IDictionary)policyMapProperty!.GetValue(options)!;

        return policyMap.Keys.Cast<string>().ToList();
    }
}
