// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The stop-turn services are registered with the lifetimes the design needs. The cancellable-skill policy of
/// LLMFunctionExecutor is an optional constructor parameter: without it nothing is cancelled, which is safe.
/// The turn scope of LLMFunctionExecutor and CorrectionTurnPreparer is a required one: without it the
/// one-time tokens of a stopped turn would survive the stop, so a missing registration must fail the
/// container instead of silently switching the discard off. Both are pinned here.
/// The registry is a singleton because the streaming request and the cancel request must share it; the run
/// state and the confirmation scope are scoped because a chat request runs exactly one turn.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class StopTurnServiceRegistrationTests
{
    private IServiceCollection _services = null!;

    [SetUp]
    public void SetUp()
    {
        _services = new ServiceCollection();
        _services.AddLLMCoreServices();
    }

    [Test]
    public void TheActiveTurnRegistry_IsASingleton()
    {
        Descriptor<IActiveTurnRegistry>().ImplementationType.ShouldBe(typeof(ActiveTurnRegistry));
        Descriptor<IActiveTurnRegistry>().Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Test]
    public void TheCancellableSkillPolicy_IsASingletonBecauseItOnlyReadsSingletons()
    {
        Descriptor<ICancellableSkillPolicy>().ImplementationType.ShouldBe(typeof(CancellableSkillPolicy));
        Descriptor<ICancellableSkillPolicy>().Lifetime.ShouldBe(ServiceLifetime.Singleton);
    }

    [Test]
    public void TheTurnRunState_IsScoped()
    {
        _services.Single(d => d.ServiceType == typeof(TurnRunState)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Test]
    public void TheTurnConfirmationScopeAndItsDiscarder_AreScoped()
    {
        Descriptor<ITurnConfirmationScope>().Lifetime.ShouldBe(ServiceLifetime.Scoped);
        Descriptor<ITurnConfirmationDiscarder>().ImplementationType.ShouldBe(typeof(TurnConfirmationDiscarder));
        Descriptor<ITurnConfirmationDiscarder>().Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [TestCase(typeof(LLMFunctionExecutor))]
    [TestCase(typeof(CorrectionTurnPreparer))]
    public void TheTurnScope_IsARequiredConstructorParameter(Type consumer)
    {
        var scopeParameters = consumer.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Where(parameter => parameter.ParameterType == typeof(ITurnConfirmationScope))
            .ToList();

        scopeParameters.ShouldNotBeEmpty();
        scopeParameters.ShouldAllBe(parameter => !parameter.HasDefaultValue && !parameter.IsOptional);
    }

    private ServiceDescriptor Descriptor<TService>() => _services.Single(d => d.ServiceType == typeof(TService));
}
