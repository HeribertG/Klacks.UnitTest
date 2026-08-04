// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TurnReflectionService — verifies that a lesson is only ever stored when the model
/// explicitly claims confidence, that anything unparsable or unconfident stores nothing at all (a wrong
/// reflection would apply to every future turn, so silence is the safe direction), and that a stored
/// lesson carries its own category, is keyed to its subject and expires.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TurnReflectionServiceTests
{
    private ICheapestModelResolver _cheapestModelResolver = null!;
    private IAgentMemoryRepository _memoryRepository = null!;
    private IEmbeddingService _embeddingService = null!;
    private ILLMProvider _provider = null!;
    private TurnReflectionService _service = null!;

    private static readonly Guid AgentId = Guid.NewGuid();

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

        _service = new TurnReflectionService(
            Substitute.For<ILogger<TurnReflectionService>>(),
            _cheapestModelResolver,
            _memoryRepository,
            _embeddingService);
    }

    private void Respond(string content, bool success = true)
    {
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>())
            .Returns(new LLMProviderResponse { Success = success, Content = content });
    }

    private static TurnReflectionRequest Request(
        string scopeKey = "add_break_placeholder",
        string whatWentWrong = "add_break_placeholder failed: unknown absence type") =>
        new(AgentId, ReflectionTriggers.SkillFailure, "Trag Anna Ferien ein", whatWentWrong, scopeKey, null);

    private async Task<List<AgentMemory>> CaptureStoredAsync(TurnReflectionRequest request)
    {
        var stored = new List<AgentMemory>();
        await _memoryRepository.AddAsync(Arg.Do<AgentMemory>(stored.Add));
        _memoryRepository.ClearReceivedCalls();
        stored.Clear();

        await _service.ReflectAsync(request);
        return stored;
    }

    [Test]
    public async Task ConfidentLesson_IsStoredAsAScopedExpiringReflection()
    {
        Respond("{\"lesson\":\"Resolve the absence type with list_absence_types before recording a placeholder.\",\"confident\":true}");

        var stored = await CaptureStoredAsync(Request());

        stored.Count.ShouldBe(1);
        stored[0].Category.ShouldBe(MemoryCategories.Reflection);
        stored[0].Key.ShouldBe("add_break_placeholder");
        stored[0].Source.ShouldBe(MemorySources.AgentSelf);
        stored[0].SourceRef.ShouldBe(ReflectionTriggers.SkillFailure);
        stored[0].Content.ShouldContain("list_absence_types");
        stored[0].ExpiresAt.ShouldNotBeNull();
        stored[0].IsPinned.ShouldBeFalse();
    }

    [Test]
    public async Task UnconfidentLesson_IsNotStored()
    {
        Respond("{\"lesson\":\"Maybe the user meant something else.\",\"confident\":false}");

        var stored = await CaptureStoredAsync(Request());

        stored.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingConfidenceFlag_IsTreatedAsUnconfident()
    {
        Respond("{\"lesson\":\"Do it differently next time.\"}");

        var stored = await CaptureStoredAsync(Request());

        // The safe direction is silence: an unfounded lesson would be injected into every later turn.
        stored.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyLesson_IsNotStored()
    {
        Respond("{\"lesson\":\"   \",\"confident\":true}");

        var stored = await CaptureStoredAsync(Request());

        stored.ShouldBeEmpty();
    }

    [Test]
    public async Task UnparsableResponse_IsNotStored()
    {
        Respond("I think the assistant should have been more careful.");

        var stored = await CaptureStoredAsync(Request());

        stored.ShouldBeEmpty();
    }

    [Test]
    public async Task FailedProviderCall_IsNotStored()
    {
        Respond(string.Empty, success: false);

        var stored = await CaptureStoredAsync(Request());

        stored.ShouldBeEmpty();
    }

    [Test]
    public async Task DuplicateLesson_IsNotStoredTwice()
    {
        Respond("{\"lesson\":\"Resolve the absence type first.\",\"confident\":true}");
        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.1f, 0.2f });
        _memoryRepository.HybridSearchAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<float[]?>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([new MemorySearchResult(
                Guid.NewGuid(), MemoryCategories.Reflection, "add_break_placeholder",
                "Resolve the absence type first.", 6, 0.95f, false)]);

        var stored = await CaptureStoredAsync(Request());

        stored.ShouldBeEmpty();
    }

    [Test]
    public async Task EmptyScopeKey_NeverReachesTheModel()
    {
        await _service.ReflectAsync(Request(scopeKey: "  "));

        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task EmptyEvidence_NeverReachesTheModel()
    {
        await _service.ReflectAsync(Request(whatWentWrong: "   "));

        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>());
    }

    [Test]
    public async Task NoModelAvailable_IsSurvivedWithoutStoring()
    {
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)null, (ILLMProvider?)null));

        var stored = await CaptureStoredAsync(Request());

        stored.ShouldBeEmpty();
    }
}
