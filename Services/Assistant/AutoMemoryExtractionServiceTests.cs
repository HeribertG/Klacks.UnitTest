// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AutoMemoryExtractionService — verifies that extracted facts containing
/// internal entity names are discarded before storage while clean facts are stored,
/// so leaked internal terminology can never re-enter the agent memory. Also verifies the
/// service resolves its cheap model exclusively through the shared ICheapestModelResolver
/// instead of duplicating that lookup.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class AutoMemoryExtractionServiceTests
{
    private ICheapestModelResolver _cheapestModelResolver = null!;
    private IAgentMemoryRepository _memoryRepository = null!;
    private IEmbeddingService _embeddingService = null!;
    private ILLMProvider _provider = null!;
    private AutoMemoryExtractionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cheapestModelResolver = Substitute.For<ICheapestModelResolver>();
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _embeddingService = Substitute.For<IEmbeddingService>();
        _provider = Substitute.For<ILLMProvider>();

        var model = new LLMModel { ModelId = "cheap-model", ApiModelId = "cheap-model" };
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)model, (ILLMProvider?)_provider));
        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((float[]?)null);

        _service = new AutoMemoryExtractionService(
            Substitute.For<ILogger<AutoMemoryExtractionService>>(),
            _cheapestModelResolver,
            _memoryRepository,
            _embeddingService);
    }

    private void SetupExtractionResponse(string jsonArray)
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>()).Returns(new LLMProviderResponse
        {
            Success = true,
            Content = jsonArray
        });
    }

    [Test]
    public async Task FactWithInternalEntityName_IsDiscarded()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Klacks_Bestellung\",\"content\":\"Eine Bestellung (OriginalOrder) wird beim Versiegeln zur SealedOrder.\",\"category\":\"procedure\",\"importance\":7}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FactWithInternalNameInKey_IsDiscarded()
    {
        SetupExtractionResponse(
            "[{\"key\":\"SealedOrder_Regel\",\"content\":\"Versiegelte Auftraege sind unveraenderlich.\",\"category\":\"procedure\",\"importance\":7}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.DidNotReceive().AddAsync(
            Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CleanFact_IsStored()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Key == "Firmen_Standort"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PersonalFact_IsScopedToCurrentUser()
    {
        var userId = Guid.NewGuid();
        SetupExtractionResponse(
            "[{\"key\":\"Lieblingssport\",\"content\":\"Der Benutzer mag Fussball.\",\"category\":\"preference\",\"importance\":6}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", userId.ToString());

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Key == "Lieblingssport" && m.UserId == userId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompanyFact_StaysGlobal_NotScopedToUser()
    {
        var userId = Guid.NewGuid();
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", userId.ToString());

        await _memoryRepository.Received(1).AddAsync(
            Arg.Is<AgentMemory>(m => m.Key == "Firmen_Standort" && m.UserId == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExtractAndStoreMemoriesAsync_ResolvesModelThroughSharedCheapestModelResolver()
    {
        SetupExtractionResponse(
            "[{\"key\":\"Firmen_Standort\",\"content\":\"Die Firma hat ihren Hauptsitz in Bern.\",\"category\":\"fact\",\"importance\":8}]");

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _cheapestModelResolver.Received(1).ResolveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoModelAvailableFromResolver_NoExtractionAttempted()
    {
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)null, (ILLMProvider?)null));

        await _service.ExtractAndStoreMemoriesAsync(Guid.NewGuid(), "frage", "antwort", "user");

        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>());
        await _memoryRepository.DidNotReceive().AddAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
    }
}
