using FluentAssertions;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace UnitTest.Infrastructure.Mediator;

[TestFixture]
public class PipelineBehaviorTests
{
    [Test]
    public async Task PipelineBehavior_IsExecutedBeforeHandler()
    {
        // Arrange
        var executionOrder = new List<string>();
        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineBehaviorTests).Assembly);
        services.AddSingleton(executionOrder);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TrackingBehavior<,>));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new TrackedRequest();

        // Act
        await mediator.Send(request);

        // Assert
        executionOrder.Should().HaveCount(3);
        executionOrder[0].Should().Be("Behavior:Before");
        executionOrder[1].Should().Be("Handler");
        executionOrder[2].Should().Be("Behavior:After");
    }

    [Test]
    public async Task PipelineBehavior_CanModifyResponse()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineBehaviorTests).Assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ResponseModifyingBehavior<,>));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new ModifiableRequest("Original");

        // Act
        var result = await mediator.Send(request);

        // Assert
        result.Should().Be("Modified: Original");
    }

    [Test]
    public async Task PipelineBehavior_CanShortCircuit()
    {
        // Arrange
        var handlerCalled = false;
        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineBehaviorTests).Assembly);
        services.AddSingleton<Action>(() => handlerCalled = true);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ShortCircuitBehavior<,>));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new ShortCircuitRequest(true);

        // Act
        var result = await mediator.Send(request);

        // Assert
        result.Should().Be("Short-circuited");
        handlerCalled.Should().BeFalse();
    }

    [Test]
    public async Task MultiplePipelineBehaviors_ExecuteInCorrectOrder()
    {
        // Arrange
        var executionOrder = new List<string>();
        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineBehaviorTests).Assembly);
        services.AddSingleton(executionOrder);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(FirstBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SecondBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ThirdBehavior<,>));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new OrderedRequest();

        // Act
        await mediator.Send(request);

        // Assert
        executionOrder.Should().ContainInOrder("First:Before", "Second:Before", "Third:Before", "Handler", "Third:After", "Second:After", "First:After");
    }

    [Test]
    public async Task PipelineBehavior_ExceptionInBehavior_PropagatesCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineBehaviorTests).Assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ThrowingBehavior<,>));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new ThrowingBehaviorRequest();

        // Act
        Func<Task> act = async () => await mediator.Send(request);

        // Assert
        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("Behavior failed") ||
                        (e.InnerException != null && e.InnerException.Message.Contains("Behavior failed")));
    }

    [Test]
    public async Task PipelineBehavior_WithNoBehaviors_HandlerExecutesDirectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineBehaviorTests).Assembly);

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new NoBehaviorRequest();

        // Act
        var result = await mediator.Send(request);

        // Assert
        result.Should().Be("Direct execution");
    }
}

#region Test Requests and Handlers for Pipeline Tests

public record TrackedRequest : IRequest<string>;

public class TrackedRequestHandler : IRequestHandler<TrackedRequest, string>
{
    private readonly List<string> _executionOrder;

    public TrackedRequestHandler(List<string> executionOrder)
    {
        _executionOrder = executionOrder;
    }

    public Task<string> Handle(TrackedRequest request, CancellationToken cancellationToken)
    {
        _executionOrder.Add("Handler");
        return Task.FromResult("Handled");
    }
}

public class TrackingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly List<string> _executionOrder;

    public TrackingBehavior(List<string> executionOrder)
    {
        _executionOrder = executionOrder;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _executionOrder.Add("Behavior:Before");
        var response = await next();
        _executionOrder.Add("Behavior:After");
        return response;
    }
}

public record ModifiableRequest(string Value) : IRequest<string>;

public class ModifiableRequestHandler : IRequestHandler<ModifiableRequest, string>
{
    public Task<string> Handle(ModifiableRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request.Value);
    }
}

public class ResponseModifyingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next();
        if (response is string str)
        {
            return (TResponse)(object)$"Modified: {str}";
        }
        return response;
    }
}

public record ShortCircuitRequest(bool ShouldShortCircuit) : IRequest<string>;

public class ShortCircuitRequestHandler : IRequestHandler<ShortCircuitRequest, string>
{
    private readonly Action? _onHandlerCalled;

    public ShortCircuitRequestHandler(Action? onHandlerCalled = null)
    {
        _onHandlerCalled = onHandlerCalled;
    }

    public Task<string> Handle(ShortCircuitRequest request, CancellationToken cancellationToken)
    {
        _onHandlerCalled?.Invoke();
        return Task.FromResult("Handler executed");
    }
}

public class ShortCircuitBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is ShortCircuitRequest { ShouldShortCircuit: true })
        {
            return Task.FromResult((TResponse)(object)"Short-circuited");
        }
        return next();
    }
}

public record OrderedRequest : IRequest<string>;

public class OrderedRequestHandler : IRequestHandler<OrderedRequest, string>
{
    private readonly List<string> _executionOrder;

    public OrderedRequestHandler(List<string> executionOrder)
    {
        _executionOrder = executionOrder;
    }

    public Task<string> Handle(OrderedRequest request, CancellationToken cancellationToken)
    {
        _executionOrder.Add("Handler");
        return Task.FromResult("Done");
    }
}

public class FirstBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly List<string> _executionOrder;

    public FirstBehavior(List<string> executionOrder)
    {
        _executionOrder = executionOrder;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _executionOrder.Add("First:Before");
        var response = await next();
        _executionOrder.Add("First:After");
        return response;
    }
}

public class SecondBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly List<string> _executionOrder;

    public SecondBehavior(List<string> executionOrder)
    {
        _executionOrder = executionOrder;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _executionOrder.Add("Second:Before");
        var response = await next();
        _executionOrder.Add("Second:After");
        return response;
    }
}

public class ThirdBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly List<string> _executionOrder;

    public ThirdBehavior(List<string> executionOrder)
    {
        _executionOrder = executionOrder;
    }

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        _executionOrder.Add("Third:Before");
        var response = await next();
        _executionOrder.Add("Third:After");
        return response;
    }
}

public record ThrowingBehaviorRequest : IRequest<string>;

public class ThrowingBehaviorRequestHandler : IRequestHandler<ThrowingBehaviorRequest, string>
{
    public Task<string> Handle(ThrowingBehaviorRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult("Should not reach here");
    }
}

public class ThrowingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Behavior failed");
    }
}

public record NoBehaviorRequest : IRequest<string>;

public class NoBehaviorRequestHandler : IRequestHandler<NoBehaviorRequest, string>
{
    public Task<string> Handle(NoBehaviorRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult("Direct execution");
    }
}

#endregion
