// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundMode: only an explicit true for Deployment:IsPlayground activates it; absent, false and
/// garbage values leave it off, the secure default of every customer installation.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Playground;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundModeTests
{
    private static bool IsActive(string? configured)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null)
        {
            values[DeploymentConstants.IsPlaygroundConfigKey] = configured;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new PlaygroundMode(configuration).IsActive;
    }

    [TestCase("true", true)]
    [TestCase("True", true)]
    [TestCase("false", false)]
    [TestCase("", false)]
    [TestCase("yes", false)]
    [TestCase("1", false)]
    [TestCase(null, false)]
    public void IsActive_FollowsTheConfigurationKey(string? configured, bool expected)
    {
        IsActive(configured).ShouldBe(expected);
    }

    [Test]
    public void IsActive_UsesTheExistingDeploymentKey()
    {
        DeploymentConstants.IsPlaygroundConfigKey.ShouldBe("Deployment:IsPlayground");
    }
}
