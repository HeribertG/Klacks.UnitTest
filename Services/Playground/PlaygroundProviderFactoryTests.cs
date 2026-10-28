// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the Playground wrapping in LLMProviderFactory: with the mode on all three getters hand out a
/// decorated provider that is wrapped exactly once; with the mode off they hand out the concrete provider and the
/// budget is never consulted.
/// </summary>

using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Generic;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Playground;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using ILLMProvider = Klacks.Api.Domain.Services.Assistant.Providers.ILLMProvider;
using LLMProvider = Klacks.Api.Domain.Models.Assistant.LLMProvider;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundProviderFactoryTests
{
    private const string ProviderId = "custom-provider";
    private const string ModelId = "custom-model";

    private ILLMRepository _repository = null!;
    private IPlaygroundMode _mode = null!;
    private IPlaygroundTokenBudget _budget = null!;
    private GenericOpenAICompatibleProvider _concrete = null!;
    private LLMProviderFactory _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _mode = Substitute.For<IPlaygroundMode>();
        _budget = Substitute.For<IPlaygroundTokenBudget>();

        var config = new LLMProvider { ProviderId = ProviderId, ProviderName = "Custom", IsEnabled = true, Priority = 1 };
        _repository.GetProviderByIdAsync(ProviderId).Returns(config);
        _repository.GetProvidersAsync().Returns(new List<LLMProvider> { config });
        _repository.GetModelByIdAsync(ModelId).Returns(new LLMModel { ModelId = ModelId, ProviderId = ProviderId, IsEnabled = true });

        _concrete = new GenericOpenAICompatibleProvider(
            new HttpClient(), NullLogger<GenericOpenAICompatibleProvider>.Instance, new ConfigurationBuilder().Build());
        var services = new ServiceCollection().AddSingleton(_concrete).BuildServiceProvider();

        _sut = new LLMProviderFactory(services, _repository, NullLogger<LLMProviderFactory>.Instance, _mode, _budget);
    }

    private static ILLMProvider Unwrap(ILLMProvider? provider)
    {
        provider.ShouldBeOfType<PlaygroundBudgetedLLMProvider>();
        var inner = ((PlaygroundBudgetedLLMProvider)provider!).Inner;
        inner.ShouldNotBeOfType<PlaygroundBudgetedLLMProvider>("the provider must be wrapped exactly once");
        return inner;
    }

    [Test]
    public async Task PlaygroundOn_GetProviderAsync_ReturnsTheDecoratedProvider()
    {
        _mode.IsActive.Returns(true);

        Unwrap(await _sut.GetProviderAsync(ProviderId)).ShouldBeSameAs(_concrete);
    }

    [Test]
    public async Task PlaygroundOn_GetProviderForModelAsync_ReturnsTheDecoratedProviderWrappedOnce()
    {
        _mode.IsActive.Returns(true);

        Unwrap(await _sut.GetProviderForModelAsync(ModelId)).ShouldBeSameAs(_concrete);
    }

    [Test]
    public async Task PlaygroundOn_GetEnabledProvidersAsync_ReturnsDecoratedProvidersWrappedOnce()
    {
        _mode.IsActive.Returns(true);

        var providers = await _sut.GetEnabledProvidersAsync();

        providers.Count.ShouldBe(1);
        Unwrap(providers[0]).ShouldBeSameAs(_concrete);
    }

    [Test]
    public async Task PlaygroundOff_AllThreeGetters_ReturnTheConcreteProviderUnwrapped()
    {
        _mode.IsActive.Returns(false);

        (await _sut.GetProviderAsync(ProviderId)).ShouldBeSameAs(_concrete);
        (await _sut.GetProviderForModelAsync(ModelId)).ShouldBeSameAs(_concrete);
        (await _sut.GetEnabledProvidersAsync()).Single().ShouldBeSameAs(_concrete);
        _budget.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task PlaygroundOff_NoProviderIsEverCharged_EvenWhenCalled()
    {
        _mode.IsActive.Returns(false);

        var provider = await _sut.GetProviderAsync(ProviderId);

        provider.ShouldNotBeOfType<PlaygroundBudgetedLLMProvider>();
        _budget.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownProvider_ReturnsNullInBothModes()
    {
        _repository.GetProviderByIdAsync("missing").Returns((LLMProvider?)null);

        _mode.IsActive.Returns(true);
        (await _sut.GetProviderAsync("missing")).ShouldBeNull();

        _mode.IsActive.Returns(false);
        (await _sut.GetProviderAsync("missing")).ShouldBeNull();
    }
}
