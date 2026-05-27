// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DiscoverProvidersQueryHandler: catalog/web merge, deduplication against existing
/// providers (by id and by normalized base URL), connectivity tagging and graceful degradation.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class DiscoverProvidersQueryHandlerTests
{
    private ILLMRepository _repository = null!;
    private IProviderWebDiscovery _webDiscovery = null!;
    private IProviderConnectivityTester _connectivityTester = null!;
    private DiscoverProvidersQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _webDiscovery = Substitute.For<IProviderWebDiscovery>();
        _connectivityTester = Substitute.For<IProviderConnectivityTester>();

        _repository.GetProvidersAsync().Returns(new List<LLMProvider>());
        _webDiscovery.DiscoverAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ProviderCandidateResource>());
        _connectivityTester.TestAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ProviderConnectivityStatus.Reachable);

        _handler = new DiscoverProvidersQueryHandler(
            _repository,
            _webDiscovery,
            _connectivityTester,
            Substitute.For<ILogger<DiscoverProvidersQueryHandler>>());
    }

    [Test]
    public async Task Handle_NoExistingNoWeb_ReturnsFullCatalogTagged()
    {
        var result = await _handler.Handle(new DiscoverProvidersQuery(), CancellationToken.None);

        result.Count.ShouldBe(KnownLlmProviderCatalog.Entries.Count);
        result.ShouldAllBe(c => c.Source == ProviderCandidateSource.Catalog);
        result.ShouldAllBe(c => c.Connectivity == ProviderConnectivityStatus.Reachable);
    }

    [Test]
    public async Task Handle_ExistingProviderId_IsDeduplicatedById()
    {
        _repository.GetProvidersAsync().Returns(new List<LLMProvider>
        {
            new() { ProviderId = "openai", ProviderName = "OpenAI", BaseUrl = "https://whatever/" }
        });

        var result = await _handler.Handle(new DiscoverProvidersQuery(), CancellationToken.None);

        result.ShouldNotContain(c => c.ProviderId == "openai");
        result.Count.ShouldBe(KnownLlmProviderCatalog.Entries.Count - 1);
    }

    [Test]
    public async Task Handle_ExistingBaseUrl_IsDeduplicatedByNormalizedUrl()
    {
        _repository.GetProvidersAsync().Returns(new List<LLMProvider>
        {
            new() { ProviderId = "my-own-openai", ProviderName = "Mine", BaseUrl = "https://api.openai.com/v1" }
        });

        var result = await _handler.Handle(new DiscoverProvidersQuery(), CancellationToken.None);

        result.ShouldNotContain(c => c.BaseUrl.Contains("api.openai.com"));
    }

    [Test]
    public async Task Handle_WebCandidate_IsAppendedAndTested()
    {
        _webDiscovery.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(new List<ProviderCandidateResource>
        {
            new()
            {
                ProviderId = "novita",
                ProviderName = "Novita AI",
                BaseUrl = "https://api.novita.ai/v3/openai/",
                Source = ProviderCandidateSource.Web,
                Connectivity = ProviderConnectivityStatus.Unknown
            }
        });

        var result = await _handler.Handle(new DiscoverProvidersQuery(), CancellationToken.None);

        var web = result.SingleOrDefault(c => c.ProviderId == "novita");
        web.ShouldNotBeNull();
        web!.Source.ShouldBe(ProviderCandidateSource.Web);
        web.Connectivity.ShouldBe(ProviderConnectivityStatus.Reachable);
    }

    [Test]
    public async Task Handle_WebCandidateDuplicatingCatalog_IsDropped()
    {
        _webDiscovery.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(new List<ProviderCandidateResource>
        {
            new()
            {
                ProviderId = "groq",
                ProviderName = "Groq (web)",
                BaseUrl = "https://api.groq.com/openai/v1/",
                Source = ProviderCandidateSource.Web
            }
        });

        var result = await _handler.Handle(new DiscoverProvidersQuery(), CancellationToken.None);

        result.Count(c => c.ProviderId == "groq").ShouldBe(1);
        result.Single(c => c.ProviderId == "groq").Source.ShouldBe(ProviderCandidateSource.Catalog);
    }
}
