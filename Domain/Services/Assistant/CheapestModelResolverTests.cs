// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for CheapestModelResolver: it selects the enabled model with the lowest combined
/// input+output cost and returns its configured provider, and returns (null, null) when no model is
/// enabled.
/// </summary>

using Providers = Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class CheapestModelResolverTests
{
    private const string CheapModelId = "cheap";
    private const string ExpensiveModelId = "expensive";

    private ILLMRepository _repository = null!;
    private ILLMProviderFactory _providerFactory = null!;
    private CheapestModelResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _providerFactory = Substitute.For<ILLMProviderFactory>();
        _resolver = new CheapestModelResolver(_repository, _providerFactory);
    }

    [Test]
    public async Task ResolveAsync_ReturnsCheapestModelAndItsProvider()
    {
        var provider = Substitute.For<Providers.ILLMProvider>();
        _repository.GetModelsAsync(true).Returns(new List<LLMModel>
        {
            new() { ModelId = ExpensiveModelId, CostPerInputToken = 1.0m, CostPerOutputToken = 1.0m },
            new() { ModelId = CheapModelId, CostPerInputToken = 0.05m, CostPerOutputToken = 0.05m }
        });
        _providerFactory.GetProviderForModelAsync(CheapModelId).Returns(provider);

        var (model, resolvedProvider) = await _resolver.ResolveAsync();

        model.ShouldNotBeNull();
        model!.ModelId.ShouldBe(CheapModelId);
        resolvedProvider.ShouldBe(provider);
    }

    [Test]
    public async Task ResolveAsync_NoEnabledModels_ReturnsNulls()
    {
        _repository.GetModelsAsync(true).Returns(new List<LLMModel>());

        var (model, provider) = await _resolver.ResolveAsync();

        model.ShouldBeNull();
        provider.ShouldBeNull();
        await _providerFactory.DidNotReceive().GetProviderForModelAsync(Arg.Any<string>());
    }
}
