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
    public async Task Defaults_AllowAnyLowOrHigherEvent()
    {
        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Low), Is.True);
        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.True);
    }

    [Test]
    public async Task Mute_BlocksAllSeverities()
    {
        await _sut.MuteAsync("u1", AgentTriggerKinds.UnstaffedShift, true);

        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.False);
    }

    [Test]
    public async Task Snooze_Future_BlocksUntilReached()
    {
        await _sut.SnoozeAsync("u1", AgentTriggerKinds.UnstaffedShift, DateTime.UtcNow.AddHours(2));

        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.False);
    }

    [Test]
    public async Task Snooze_Past_ClearsBlock()
    {
        await _sut.SnoozeAsync("u1", AgentTriggerKinds.UnstaffedShift, DateTime.UtcNow.AddHours(-1));

        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Low), Is.True);
    }

    [Test]
    public async Task SetMinimumSeverity_BlocksLowerEvents()
    {
        await _sut.SetMinimumSeverityAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High);

        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Low), Is.False);
        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.Medium), Is.False);
        Assert.That(await _sut.IsAllowedAsync("u1", AgentTriggerKinds.UnstaffedShift, AgentTriggerSeverity.High), Is.True);
    }

    [Test]
    public void SetMinimumSeverity_UnknownValueThrows()
    {
        Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.SetMinimumSeverityAsync("u1", AgentTriggerKinds.UnstaffedShift, "extreme"));
    }
}
