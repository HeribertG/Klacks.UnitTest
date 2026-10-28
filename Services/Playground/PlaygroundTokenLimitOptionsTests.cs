// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the fail-closed configuration of the Playground token limit: valid values are taken over, while
/// missing, empty, garbage, zero and negative values keep the defaults instead of meaning "unlimited", and the
/// environment-variable spelling Playground__TokenLimit__X reaches the options.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Playground;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundTokenLimitOptionsTests
{
    private static PlaygroundTokenLimitOptions Load(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new PlaygroundTokenLimitOptions();
        options.ApplyConfiguration(configuration.GetSection(PlaygroundTokenLimitOptions.SectionName));
        return options;
    }

    [Test]
    public void ApplyConfiguration_ValidValues_AreTakenOver()
    {
        var options = Load(new()
        {
            ["Playground:TokenLimit:GlobalDailyTokens"] = "5000",
            ["Playground:TokenLimit:PerIpDailyTokens"] = "600",
            ["Playground:TokenLimit:PerDeviceDailyTokens"] = "70",
        });

        options.GlobalDailyTokens.ShouldBe(5000);
        options.PerIpDailyTokens.ShouldBe(600);
        options.PerDeviceDailyTokens.ShouldBe(70);
    }

    [Test]
    public void ApplyConfiguration_NothingConfigured_KeepsTheDefaults()
    {
        var options = Load(new());

        options.GlobalDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultGlobalDailyTokens);
        options.PerIpDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultPerIpDailyTokens);
        options.PerDeviceDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultPerDeviceDailyTokens);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("abc")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("1.5")]
    [TestCase("99999999999999999999")]
    public void ApplyConfiguration_UnusableValue_FallsBackToTheDefaults(string garbage)
    {
        var options = Load(new()
        {
            ["Playground:TokenLimit:GlobalDailyTokens"] = garbage,
            ["Playground:TokenLimit:PerIpDailyTokens"] = garbage,
            ["Playground:TokenLimit:PerDeviceDailyTokens"] = garbage,
        });

        options.GlobalDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultGlobalDailyTokens);
        options.PerIpDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultPerIpDailyTokens);
        options.PerDeviceDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultPerDeviceDailyTokens);
    }

    [Test]
    public void ApplyConfiguration_OneBadValue_DoesNotAffectTheOthers()
    {
        var options = Load(new()
        {
            ["Playground:TokenLimit:GlobalDailyTokens"] = "oops",
            ["Playground:TokenLimit:PerIpDailyTokens"] = "600",
        });

        options.GlobalDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultGlobalDailyTokens);
        options.PerIpDailyTokens.ShouldBe(600);
        options.PerDeviceDailyTokens.ShouldBe(PlaygroundTokenLimitConstants.DefaultPerDeviceDailyTokens);
    }

    [Test]
    public void ApplyConfiguration_EnvironmentVariableSpelling_ReachesTheOptions()
    {
        const string Variable = "Playground__TokenLimit__PerIpDailyTokens";
        Environment.SetEnvironmentVariable(Variable, "1234");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var options = new PlaygroundTokenLimitOptions();

            options.ApplyConfiguration(configuration.GetSection(PlaygroundTokenLimitOptions.SectionName));

            options.PerIpDailyTokens.ShouldBe(1234);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }
}
