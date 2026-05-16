// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InMemoryAgentTriggerPreferenceService.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class InMemoryAgentTriggerPreferenceServiceTests
{
    private InMemoryAgentTriggerPreferenceService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new InMemoryAgentTriggerPreferenceService();
    }

    [Test]
    public void Defaults_AllowAnyLowOrHigherEvent()
    {
        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Low), Is.True);
        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.True);
    }

    [Test]
    public void Mute_BlocksAllSeverities()
    {
        _sut.Mute("u1", AgentTriggerKinds.UnstaffedShift, true);

        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.False);
    }

    [Test]
    public void Snooze_Future_BlocksUntilReached()
    {
        _sut.Snooze("u1", AgentTriggerKinds.UnstaffedShift, DateTime.UtcNow.AddHours(2));

        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.False);
    }

    [Test]
    public void Snooze_Past_ClearsBlock()
    {
        _sut.Snooze("u1", AgentTriggerKinds.UnstaffedShift, DateTime.UtcNow.AddHours(-1));

        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Low), Is.True);
    }

    [Test]
    public void SetMinimumSeverity_BlocksLowerEvents()
    {
        _sut.SetMinimumSeverity("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High);

        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Low), Is.False);
        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium), Is.False);
        Assert.That(_sut.IsAllowed("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.True);
    }

    [Test]
    public void SetMinimumSeverity_UnknownValueThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.SetMinimumSeverity("u1", AgentTriggerKinds.UnstaffedShift, "extreme"));
    }
}
