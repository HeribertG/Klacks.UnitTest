// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AgentTriggerService — verifies notification dispatch per connected user,
/// rate-limit gating, severity-tag prefix and graceful handling when no users are connected.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AgentTriggerServiceTests
{
    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private IAssistantNotificationService _notificationService = null!;
    private AgentTriggerService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();
        _notificationService = Substitute.For<IAssistantNotificationService>();
        _sut = new AgentTriggerService(_rateLimiter, _notificationService,
            NullLogger<AgentTriggerService>.Instance);
    }

    private static UnstaffedShiftTriggerEvent MakeEvent(int daysUntil = 2) =>
        new(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysUntil)), daysUntil, null);

    [Test]
    public async Task OnEventAsync_NoConnectedUsers_SkipsDispatch()
    {
        _notificationService.GetConnectedUserIds().Returns(Array.Empty<string>());

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.DidNotReceiveWithAnyArgs().SendProactiveMessageAsync(default!, default!);
    }

    [Test]
    public async Task OnEventAsync_DispatchesToEachConnectedUser_AndRecordsFire()
    {
        var users = new[] { "user-a", "user-b" };
        _notificationService.GetConnectedUserIds().Returns(users);
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>());
        await _notificationService.Received(1).SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>());
        _rateLimiter.Received(1).RecordFire("user-a", AgentTriggerKinds.UnstaffedShift);
        _rateLimiter.Received(1).RecordFire("user-b", AgentTriggerKinds.UnstaffedShift);
    }

    [Test]
    public async Task OnEventAsync_RateLimited_SkipsThatUser()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a", "user-b" });
        _rateLimiter.ShouldFire("user-a", Arg.Any<string>()).Returns(true);
        _rateLimiter.ShouldFire("user-b", Arg.Any<string>()).Returns(false);

        await _sut.OnEventAsync(MakeEvent());

        await _notificationService.Received(1).SendProactiveMessageAsync("user-a", Arg.Any<string>(), Arg.Any<string?>());
        await _notificationService.DidNotReceive().SendProactiveMessageAsync("user-b", Arg.Any<string>(), Arg.Any<string?>());
    }

    [Test]
    public async Task OnEventAsync_PrefixesHighSeverityWithTag()
    {
        _notificationService.GetConnectedUserIds().Returns(new[] { "user-a" });
        _rateLimiter.ShouldFire(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        await _sut.OnEventAsync(MakeEvent(daysUntil: 1));

        await _notificationService.Received(1).SendProactiveMessageAsync(
            "user-a",
            Arg.Is<string>(s => s.StartsWith("[HIGH]")),
            Arg.Any<string?>());
    }
}

[TestFixture]
public class AgentTriggerRateLimiterTests
{
    private AgentTriggerRateLimiter _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new AgentTriggerRateLimiter();
    }

    [Test]
    public void FreshUser_HasFullDailyBudget()
    {
        Assert.That(_sut.ShouldFire("user-a", "kind"), Is.True);
        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(5));
    }

    [Test]
    public void RecordFire_DecrementsBudget()
    {
        _sut.RecordFire("user-a", "kind");

        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(4));
    }

    [Test]
    public void ExceededBudget_ShouldFireReturnsFalse()
    {
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", "kind");
        }

        Assert.That(_sut.ShouldFire("user-a", "kind"), Is.False);
        Assert.That(_sut.GetRemainingBudget("user-a", "kind"), Is.EqualTo(0));
    }

    [Test]
    public void IndependentKindsHaveIndependentBudgets()
    {
        for (var i = 0; i < 5; i++)
        {
            _sut.RecordFire("user-a", "kind-1");
        }

        Assert.That(_sut.ShouldFire("user-a", "kind-1"), Is.False);
        Assert.That(_sut.ShouldFire("user-a", "kind-2"), Is.True);
    }
}
