using FluentAssertions;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.Mediator;

[TestFixture]
public class ServiceCollectionExtensionsTests
{
    [Test]
    public void AddMediator_RegistersIMediator()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddMediator(typeof(ServiceCollectionExtensionsTests).Assembly);

        // Assert
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetService<IMediator>();
        mediator.Should().NotBeNull();
        mediator.Should().BeOfType<Klacks.Api.Infrastructure.Mediator.Mediator>();
    }

    [Test]
    public void AddMediator_RegistersHandlersFromAssembly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddMediator(typeof(ServiceCollectionExtensionsTests).Assembly);

        // Assert
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetService<IRequestHandler<RegistrationTestRequest, string>>();
        handler.Should().NotBeNull();
    }

    [Test]
    public void AddMediator_WithMultipleAssemblies_RegistersAllHandlers()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddMediator(
            typeof(ServiceCollectionExtensionsTests).Assembly,
            typeof(Klacks.Api.Infrastructure.Mediator.IMediator).Assembly);

        // Assert
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetService<IMediator>();
        mediator.Should().NotBeNull();
    }

    [Test]
    public void AddPipelineBehavior_RegistersBehavior()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(ServiceCollectionExtensionsTests).Assembly);

        // Act
        services.AddPipelineBehavior(typeof(TestPipelineBehavior<,>));

        // Assert
        using var provider = services.BuildServiceProvider();
        var behaviorType = typeof(IPipelineBehavior<RegistrationTestRequest, string>);
        var behaviors = provider.GetServices(behaviorType);
        behaviors.Should().ContainSingle();
    }

    [Test]
    public void AddMediator_MediatorIsScoped()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(ServiceCollectionExtensionsTests).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act
        IMediator? mediator1, mediator2;
        using (var scope1 = provider.CreateScope())
        {
            mediator1 = scope1.ServiceProvider.GetService<IMediator>();
        }
        using (var scope2 = provider.CreateScope())
        {
            mediator2 = scope2.ServiceProvider.GetService<IMediator>();
        }

        // Assert
        mediator1.Should().NotBeNull();
        mediator2.Should().NotBeNull();
        mediator1.Should().NotBeSameAs(mediator2);
    }

    [Test]
    public void AddMediator_HandlersAreScoped()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(ServiceCollectionExtensionsTests).Assembly);
        using var provider = services.BuildServiceProvider();

        // Act
        IRequestHandler<RegistrationTestRequest, string>? handler1, handler2;
        using (var scope1 = provider.CreateScope())
        {
            handler1 = scope1.ServiceProvider.GetService<IRequestHandler<RegistrationTestRequest, string>>();
        }
        using (var scope2 = provider.CreateScope())
        {
            handler2 = scope2.ServiceProvider.GetService<IRequestHandler<RegistrationTestRequest, string>>();
        }

        // Assert
        handler1.Should().NotBeNull();
        handler2.Should().NotBeNull();
        handler1.Should().NotBeSameAs(handler2);
    }

    [Test]
    public async Task AddMediator_EndToEndIntegration_Works()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddMediator(typeof(ServiceCollectionExtensionsTests).Assembly);
        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();
        var request = new RegistrationTestRequest("Integration Test");

        // Act
        var result = await mediator.Send(request);

        // Assert
        result.Should().Be("Processed: Integration Test");
    }
}

#region Test Types for Registration

public record RegistrationTestRequest(string Input) : IRequest<string>;

public class RegistrationTestRequestHandler : IRequestHandler<RegistrationTestRequest, string>
{
    public Task<string> Handle(RegistrationTestRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult($"Processed: {request.Input}");
    }
}

public class TestPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        return next();
    }
}

#endregion
