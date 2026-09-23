// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessengerIntentQueue — verifies that queued messages reach the reader in order, that
/// a full queue drops the message (TryEnqueue false, warning logged) instead of blocking or throwing,
/// and that the queue stays a dependency leaf (logger only), which is what actually keeps
/// MessagingService -> MessengerIntentObserver -> queue free of a DI cycle.
/// </summary>

using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessengerIntentQueueTests
{
    private static InboundClientMessengerMessage Message() => new(
        MessageId: Guid.NewGuid(),
        ClientId: Guid.NewGuid(),
        Channel: "Telegram",
        Sender: "12345",
        SenderDisplayName: "Jane Doe",
        Content: "I'm sick today",
        ReceivedAt: DateTime.UtcNow);

    [Test]
    public void Constructor_TakesOnlyTheLogger_SoTheQueueStaysADependencyLeaf()
    {
        var parameterTypes = typeof(MessengerIntentQueue).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        Assert.That(parameterTypes, Is.EquivalentTo(new[] { typeof(ILogger<MessengerIntentQueue>) }));
    }

    [Test]
    public async Task TryEnqueue_DeliversMessagesToTheReaderInOrder()
    {
        var queue = new MessengerIntentQueue(NullLogger<MessengerIntentQueue>.Instance);
        var first = Message();
        var second = Message();

        queue.TryEnqueue(first).ShouldBeTrue();
        queue.TryEnqueue(second).ShouldBeTrue();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<InboundClientMessengerMessage>();
        await foreach (var message in queue.ReadAllAsync(cts.Token))
        {
            received.Add(message);
            if (received.Count == 2)
            {
                break;
            }
        }

        received.ShouldBe(new[] { first, second });
    }

    [Test]
    public void TryEnqueue_QueueFull_DropsTheMessageWithoutThrowingAndLogsAWarning()
    {
        var logger = Substitute.For<ILogger<MessengerIntentQueue>>();
        var queue = new MessengerIntentQueue(logger);
        for (var i = 0; i < MessengerIntentQueue.Capacity; i++)
        {
            queue.TryEnqueue(Message()).ShouldBeTrue();
        }

        var accepted = queue.TryEnqueue(Message());

        accepted.ShouldBeFalse();
        logger.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && call.GetArguments()[0] is LogLevel level
                && level == LogLevel.Warning)
            .ShouldBe(1);
    }
}
