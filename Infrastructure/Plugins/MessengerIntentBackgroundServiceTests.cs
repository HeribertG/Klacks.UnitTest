// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessengerIntentBackgroundService — verifies that the consumer drains
/// IMessengerIntentQueue through IMessengerIntentProcessor resolved from a DI scope, and that a failure
/// while processing one message is swallowed so the next queued message is still processed (a single
/// failing message must never take the consumer, or the host, down).
/// </summary>

using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Plugin.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Plugins;

[TestFixture]
public class MessengerIntentBackgroundServiceTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private IMessengerIntentProcessor _processor = null!;
    private MessengerIntentQueue _queue = null!;
    private ServiceProvider _serviceProvider = null!;
    private MessengerIntentBackgroundService? _sut;

    [SetUp]
    public void SetUp()
    {
        _processor = Substitute.For<IMessengerIntentProcessor>();
        _queue = new MessengerIntentQueue(NullLogger<MessengerIntentQueue>.Instance);

        var services = new ServiceCollection();
        services.AddScoped(_ => _processor);
        _serviceProvider = services.BuildServiceProvider();

        _sut = new MessengerIntentBackgroundService(
            _queue,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MessengerIntentBackgroundService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_sut != null)
        {
            await _sut.StopAsync(CancellationToken.None);
            _sut.Dispose();
        }

        await _serviceProvider.DisposeAsync();
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
    public async Task QueuedMessage_IsProcessedByTheScopedProcessor()
    {
        var message = Message();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processor.ProcessAsync(message, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                processed.TrySetResult();
                return Task.CompletedTask;
            });

        await _sut!.StartAsync(CancellationToken.None);
        _queue.TryEnqueue(message).ShouldBeTrue();

        var finished = await Task.WhenAny(processed.Task, Task.Delay(CompletionTimeout));

        finished.ShouldBe(processed.Task, "the consumer must hand the queued message to the processor");
    }

    [Test]
    public async Task ProcessorThrows_ConsumerKeepsRunningAndProcessesTheNextMessage()
    {
        var failing = Message();
        var next = Message();
        var nextProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processor.ProcessAsync(failing, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("boom")));
        _processor.ProcessAsync(next, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                nextProcessed.TrySetResult();
                return Task.CompletedTask;
            });

        await _sut!.StartAsync(CancellationToken.None);
        _queue.TryEnqueue(failing).ShouldBeTrue();
        _queue.TryEnqueue(next).ShouldBeTrue();

        var finished = await Task.WhenAny(nextProcessed.Task, Task.Delay(CompletionTimeout));

        finished.ShouldBe(nextProcessed.Task, "a failing message must not stop the consumer loop");
        _sut.ExecuteTask!.IsCompleted.ShouldBeFalse("the consumer loop must still be running");
    }
}
