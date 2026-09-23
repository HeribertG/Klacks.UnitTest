// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessengerIntentObserver — verifies that the observer only hands the message to
/// IMessengerIntentQueue and returns an already completed task (the analysis itself runs decoupled in
/// MessengerIntentBackgroundService, see MessengerIntentProcessorTests), that a full queue never
/// surfaces as an exception to the messaging plugin, and that its constructor stays free of kernel
/// services so MessagingService cannot close a DI cycle through it.
/// </summary>

using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessengerIntentObserverTests
{
    private IMessengerIntentQueue _queue = null!;
    private MessengerIntentObserver _observer = null!;

    [SetUp]
    public void SetUp()
    {
        _queue = Substitute.For<IMessengerIntentQueue>();
        _observer = new MessengerIntentObserver(_queue, Substitute.For<ILogger<MessengerIntentObserver>>());
    }

    [Test]
    public void Constructor_TakesNoKernelServices_SoMessagingServiceCannotCloseADiCycle()
    {
        var parameterTypes = typeof(MessengerIntentObserver).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        Assert.That(parameterTypes, Is.EquivalentTo(new[]
        {
            typeof(IMessengerIntentQueue),
            typeof(ILogger<MessengerIntentObserver>)
        }));
    }

    private static InboundClientMessengerMessage Message() => new(
        MessageId: Guid.NewGuid(),
        ClientId: Guid.NewGuid(),
        Channel: "Telegram",
        Sender: "12345",
        SenderDisplayName: "Jane Doe",
        Content: "I'm sick today",
        ReceivedAt: DateTime.UtcNow);

    [Test]
    public async Task OnInboundMessageAsync_EnqueuesTheMessageAndReturnsImmediately()
    {
        var message = Message();
        _queue.TryEnqueue(message).Returns(true);

        var task = _observer.OnInboundMessageAsync(message);

        Assert.That(task.IsCompletedSuccessfully, Is.True, "the observer must not await any analysis work");
        await task;
        _queue.Received(1).TryEnqueue(message);
    }

    [Test]
    public async Task OnInboundMessageAsync_QueueFull_ReturnsWithoutThrowing()
    {
        var message = Message();
        _queue.TryEnqueue(message).Returns(false);

        await _observer.OnInboundMessageAsync(message);

        _queue.Received(1).TryEnqueue(message);
    }
}
