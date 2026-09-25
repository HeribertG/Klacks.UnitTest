// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroOutputChannelPolicy: supported literal channels pass, an unsupported channel, a
/// computed channel and a failed scan each produce an actionable refusal.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Macros;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroOutputChannelPolicyTests
{
    [Test]
    public void SupportedChannels_Pass()
    {
        MacroOutputChannelPolicy.FindViolation(new MacroOutputChannelScan(new[] { 1, 10, 14 }, 0, null)).ShouldBeNull();
    }

    [Test]
    public void UnsupportedChannel_IsNamedInTheRefusal()
    {
        var violation = MacroOutputChannelPolicy.FindViolation(new MacroOutputChannelScan(new[] { 1, 99, 99, 5 }, 0, null));

        violation.ShouldNotBeNull();
        violation.ShouldContain("5, 99");
        violation.ShouldContain("silently discarded");
    }

    [Test]
    public void ComputedChannel_IsRefused()
    {
        var violation = MacroOutputChannelPolicy.FindViolation(new MacroOutputChannelScan(new[] { 1 }, 2, null));

        violation.ShouldNotBeNull();
        violation.ShouldContain("plain number");
    }

    [Test]
    public void FailedScan_IsRefused()
    {
        var violation = MacroOutputChannelPolicy.FindViolation(MacroOutputChannelScan.Failure("line 2: Unknown symbol"));

        violation.ShouldNotBeNull();
        violation.ShouldContain("could not be analysed");
    }
}
