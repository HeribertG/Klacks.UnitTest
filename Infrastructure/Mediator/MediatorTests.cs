using FluentAssertions;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace UnitTest.Infrastructure.Mediator;

[TestFixture]
public class MediatorTests
{
    private ServiceProvider _serviceProvider = null!;
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(MediatorTests).Assembly);
        _serviceProvider = services.BuildServiceProvider();
        _mediator = _serviceProvider.GetRequiredService<IMediator>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
    }

    [Test]
    public async Task Send_WithResponse_ReturnsHandlerResult()
    {
        // Arrange
        var request = new TestQueryWithResponse("TestValue");

        // Act
        var result = await _mediator.Send(request);

        // Assert
        result.Should().Be("Handled: TestValue");
    }

    [Test]
    public async Task Send_WithoutResponse_ReturnsUnit()
    {
        // Arrange
        var request = new TestCommandWithoutResponse();

        // Act
        var result = await _mediator.Send(request);

        // Assert
        result.Should().Be(Unit.Value);
    }

    [Test]
    public async Task Send_WithComplexResponse_ReturnsComplexObject()
    {
        // Arrange
        var request = new TestQueryWithComplexResponse(42, "Test");

        // Act
        var result = await _mediator.Send(request);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(42);
        result.Name.Should().Be("Test");
        result.ProcessedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Test]
    public void Send_WithUnregisteredHandler_ThrowsInvalidOperationException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<IMediator, Klacks.Api.Infrastructure.Mediator.Mediator>();
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new UnregisteredRequest();

        // Act
        Func<Task> act = async () => await mediator.Send(request);

        // Assert
        act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No handler registered*");
    }

    [Test]
    public void Send_WithNullRequest_ThrowsArgumentNullException()
    {
        // Arrange
        TestQueryWithResponse? request = null;

        // Act
        Func<Task> act = async () => await _mediator.Send(request!);

        // Assert
        act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task Send_WithCancellationToken_PassesTokenToHandler()
    {
        // Arrange
        var request = new TestQueryWithCancellation();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        // Act
        var result = await _mediator.Send(request, token);

        // Assert
        result.Should().BeFalse();
    }

    [Test]
    public async Task Send_WithCancelledToken_HandlerReceivesCancelledToken()
    {
        // Arrange
        var request = new TestQueryWithCancellation();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _mediator.Send(request, cts.Token);

        // Assert
        result.Should().BeTrue();
    }

    [Test]
    public async Task Send_WithMultipleRequests_HandlesAllCorrectly()
    {
        // Arrange
        var requests = Enumerable.Range(1, 10)
            .Select(i => new TestQueryWithResponse($"Value{i}"))
            .ToList();

        // Act
        var results = await Task.WhenAll(requests.Select(r => _mediator.Send(r)));

        // Assert
        results.Should().HaveCount(10);
        for (int i = 0; i < 10; i++)
        {
            results[i].Should().Be($"Handled: Value{i + 1}");
        }
    }

    [Test]
    public async Task Send_HandlerThrowsException_PropagatesException()
    {
        // Arrange
        var request = new TestQueryThatThrows();

        // Act
        Func<Task> act = async () => await _mediator.Send(request);

        // Assert
        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("Handler intentionally failed") ||
                        (e.InnerException != null && e.InnerException.Message.Contains("Handler intentionally failed")));
    }

    [Test]
    public async Task Send_WithAsyncHandler_CompletesAsynchronously()
    {
        // Arrange
        var request = new TestAsyncQuery(100);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var result = await _mediator.Send(request);
        stopwatch.Stop();

        // Assert
        result.Should().Be("Async completed after 100ms");
        stopwatch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(90);
    }
}

#region Test Requests and Handlers

public record TestQueryWithResponse(string Value) : IRequest<string>;

public class TestQueryWithResponseHandler : IRequestHandler<TestQueryWithResponse, string>
{
    public Task<string> Handle(TestQueryWithResponse request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Handled: {request.Value}");
    }
}

public record TestCommandWithoutResponse : IRequest;

public class TestCommandWithoutResponseHandler : IRequestHandler<TestCommandWithoutResponse, Unit>
{
    public Task<Unit> Handle(TestCommandWithoutResponse request, CancellationToken cancellationToken)
    {
        return Unit.Task;
    }
}

public record TestQueryWithComplexResponse(int Id, string Name) : IRequest<ComplexResponse>;

public record ComplexResponse(int Id, string Name, DateTime ProcessedAt);

public class TestQueryWithComplexResponseHandler : IRequestHandler<TestQueryWithComplexResponse, ComplexResponse>
{
    public Task<ComplexResponse> Handle(TestQueryWithComplexResponse request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ComplexResponse(request.Id, request.Name, DateTime.UtcNow));
    }
}

public record UnregisteredRequest : IRequest<string>;

public record TestQueryWithCancellation : IRequest<bool>;

public class TestQueryWithCancellationHandler : IRequestHandler<TestQueryWithCancellation, bool>
{
    public Task<bool> Handle(TestQueryWithCancellation request, CancellationToken cancellationToken)
    {
        return Task.FromResult(cancellationToken.IsCancellationRequested);
    }
}

public record TestQueryThatThrows : IRequest<string>;

public class TestQueryThatThrowsHandler : IRequestHandler<TestQueryThatThrows, string>
{
    public Task<string> Handle(TestQueryThatThrows request, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Handler intentionally failed");
    }
}

public record TestAsyncQuery(int DelayMs) : IRequest<string>;

public class TestAsyncQueryHandler : IRequestHandler<TestAsyncQuery, string>
{
    public async Task<string> Handle(TestAsyncQuery request, CancellationToken cancellationToken)
    {
        await Task.Delay(request.DelayMs, cancellationToken);
        return $"Async completed after {request.DelayMs}ms";
    }
}

#endregion
