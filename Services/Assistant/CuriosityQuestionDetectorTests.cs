// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CuriosityQuestionDetector — covers the opt-in flag (off by default),
/// per-user targeting, skipping users whose daily budget is spent, skipping non-GUID user ids,
/// and skipping a topic the user has already revealed so Klacksy never asks the same thing twice.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class CuriosityQuestionDetectorTests
{
    private IAssistantNotificationService _notification = null!;
    private IAgentMemoryRepository _memory = null!;
    private IAgentRepository _agents = null!;
    private IAgentTriggerRateLimiter _rateLimiter = null!;
    private Agent _agent = null!;

    [SetUp]
    public void Setup()
    {
        _notification = Substitute.For<IAssistantNotificationService>();
        _memory = Substitute.For<IAgentMemoryRepository>();
        _agents = Substitute.For<IAgentRepository>();
        _rateLimiter = Substitute.For<IAgentTriggerRateLimiter>();

        _agent = new Agent { Id = Guid.NewGuid() };
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(_agent);
        _rateLimiter.GetRemainingBudget(Arg.Any<string>(), Arg.Any<string>()).Returns(1);
        _memory.GetByUserAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
    }

    private static IConfiguration Config(bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CuriosityQuestionDetector.EnabledConfigKey] = enabled.ToString()
            })
            .Build();

    private CuriosityQuestionDetector Detector(bool enabled) =>
        new(_notification, _memory, _agents, _rateLimiter, Config(enabled),
            NullLogger<CuriosityQuestionDetector>.Instance);

    [Test]
    public async Task Disabled_EmitsNothing_EvenWithConnectedUser()
    {
        _notification.GetConnectedUserIds().Returns(new[] { Guid.NewGuid().ToString() });

        var events = await Detector(enabled: false).DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task Enabled_ConnectedUserWithNoMemories_EmitsOneTargetedQuestion()
    {
        var userId = Guid.NewGuid();
        _notification.GetConnectedUserIds().Returns(new[] { userId.ToString() });

        var events = await Detector(enabled: true).DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var curiosity = events[0] as CuriosityQuestionTriggerEvent;
        Assert.That(curiosity, Is.Not.Null);
        Assert.That(curiosity!.UserId, Is.EqualTo(userId));
        Assert.That(curiosity.TargetUserId, Is.EqualTo(userId));
        Assert.That(curiosity.Kind, Is.EqualTo(AgentTriggerKinds.CuriosityQuestion));
    }

    [Test]
    public async Task Enabled_TopicAlreadyRevealed_SkipsThatTopic()
    {
        var userId = Guid.NewGuid();
        _notification.GetConnectedUserIds().Returns(new[] { userId.ToString() });
        _memory.GetByUserAsync(_agent.Id, userId, Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>
            {
                new() { Key = "hobby", Content = "The user plays football every weekend." }
            });

        var events = await Detector(enabled: true).DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var question = ((CuriosityQuestionTriggerEvent)events[0]).Question;
        var sportText = CuriosityQuestions.Pool.First(q => q.Topic == "sport").Text;
        Assert.That(question, Is.Not.EqualTo(sportText));
    }

    [Test]
    public async Task Enabled_BudgetSpent_SkipsUser()
    {
        var userId = Guid.NewGuid();
        _notification.GetConnectedUserIds().Returns(new[] { userId.ToString() });
        _rateLimiter.GetRemainingBudget(userId.ToString(), AgentTriggerKinds.CuriosityQuestion).Returns(0);

        var events = await Detector(enabled: true).DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task Enabled_NonGuidUserId_IsSkipped()
    {
        _notification.GetConnectedUserIds().Returns(new[] { "not-a-guid" });

        var events = await Detector(enabled: true).DetectAsync();

        Assert.That(events, Is.Empty);
    }
}
