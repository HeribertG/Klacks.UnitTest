// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the defaults of BackgroundServiceOptions that other features silently depend on. The escalation
/// chain sweep is what expires stages and exhausts unanswered chains; the proactive approval chain
/// (design 2026-09-20) starts chains from the heartbeat, so an installation that never sets the flag must
/// get the sweep - otherwise an approval request would hang on its first stage forever.
/// </summary>

using Klacks.Api.Application.Configuration;

namespace Klacks.UnitTest.Application.Configuration;

[TestFixture]
public class BackgroundServiceOptionsDefaultsTests
{
    [Test]
    public void EscalationChain_IsOnByDefault()
    {
        var options = new BackgroundServiceOptions();

        Assert.That(options.EscalationChain, Is.True, "The approval chain relies on the sweep; it must run unless explicitly switched off.");
    }

    [Test]
    public void EscalationChain_CanStillBeSwitchedOff()
    {
        var options = new BackgroundServiceOptions { EscalationChain = false };

        Assert.That(options.EscalationChain, Is.False);
    }
}
