// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ConversationCompactionService: a valid structured JSON model response is stored
/// as structured JSON, a non-JSON response falls back to truncated free text, an existing legacy
/// free-text summary is migrated into the structured facts on the next run, and the compaction is
/// skipped below the message-count threshold or when there are no old messages to summarize.
/// </summary>

using Microsoft.Extensions.Logging;
using Providers = Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ConversationCompactionServiceTests
{
    private const string ConvId = "conv-1";
    private const int ReadyMessageCount = 40;
    private const string CheapModelId = "m-1";

    private const string StructuredResponse =
        "{\"openTasks\":[\"Finish the roster\"],\"decisions\":[\"Use the night shift\"],\"facts\":[\"Prefers mornings\"]}";

    private ILLMRepository _repository = null!;
    private ILLMProviderFactory _providerFactory = null!;
    private Providers.ILLMProvider _provider = null!;
    private ConversationCompactionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _providerFactory = Substitute.For<ILLMProviderFactory>();
        _provider = Substitute.For<Providers.ILLMProvider>();
        _service = new ConversationCompactionService(
            Substitute.For<ILogger<ConversationCompactionService>>(),
            _providerFactory,
            _repository);
    }

    private LLMConversation ArrangeReadyConversation(string? existingSummary, int messageCount = ReadyMessageCount)
    {
        var conversation = new LLMConversation
        {
            ConversationId = ConvId,
            MessageCount = messageCount,
            Summary = existingSummary
        };

        _repository.GetConversationByConversationIdAsync(ConvId).Returns(conversation);
        _repository.GetOldestMessagesAsync(ConvId, Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<LLMMessage>
            {
                new() { Role = "user", Content = "How do I plan the week?" },
                new() { Role = "assistant", Content = "Let us start with the roster." }
            });
        _repository.GetModelsAsync(true).Returns(new List<LLMModel>
        {
            new()
            {
                ModelId = CheapModelId,
                ApiModelId = "api-m-1",
                CostPerInputToken = 0.1m,
                CostPerOutputToken = 0.1m
            }
        });
        _providerFactory.GetProviderForModelAsync(CheapModelId).Returns(_provider);

        return conversation;
    }

    private void ArrangeProviderResponse(string content)
    {
        _provider.ProcessAsync(Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new Providers.LLMProviderResponse { Success = true, Content = content });
    }

    [Test]
    public async Task StructuredResponse_StoredAsStructuredJson()
    {
        var conversation = ArrangeReadyConversation(existingSummary: null);
        ArrangeProviderResponse(StructuredResponse);

        await _service.CompactIfNeededAsync(ConvId);

        ConversationSummaryCodec.TryParse(conversation.Summary, out var stored).ShouldBeTrue();
        stored.OpenTasks.ShouldContain("Finish the roster");
        stored.Facts.ShouldContain("Prefers mornings");
        await _repository.Received(1).UpdateConversationAsync(conversation);
    }

    [Test]
    public async Task NonJsonResponse_FallsBackToFreeText()
    {
        var conversation = ArrangeReadyConversation(existingSummary: null);
        const string plain = "The user discussed weekly shift planning and rest days.";
        ArrangeProviderResponse(plain);

        await _service.CompactIfNeededAsync(ConvId);

        conversation.Summary.ShouldBe(plain);
        ConversationSummaryCodec.TryParse(conversation.Summary, out _).ShouldBeFalse();
    }

    [Test]
    public async Task LegacyFreeTextSummary_MigratedIntoFacts()
    {
        const string legacy = "The user is a nurse in Bern and works night shifts.";
        var conversation = ArrangeReadyConversation(existingSummary: legacy);
        ArrangeProviderResponse(StructuredResponse);

        await _service.CompactIfNeededAsync(ConvId);

        ConversationSummaryCodec.TryParse(conversation.Summary, out var stored).ShouldBeTrue();
        stored.Facts.ShouldContain(legacy);
        stored.Facts.ShouldContain("Prefers mornings");
    }

    [Test]
    public async Task BelowThreshold_DoesNothing()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old", messageCount: 5);

        await _service.CompactIfNeededAsync(ConvId);

        conversation.Summary.ShouldBe("old");
        await _provider.DidNotReceive()
            .ProcessAsync(Arg.Any<Providers.LLMProviderRequest>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().UpdateConversationAsync(Arg.Any<LLMConversation>());
    }

    [Test]
    public async Task NoOldMessages_DoesNothing()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old");
        _repository.GetOldestMessagesAsync(ConvId, Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<LLMMessage>());

        await _service.CompactIfNeededAsync(ConvId);

        conversation.Summary.ShouldBe("old");
        await _repository.DidNotReceive().UpdateConversationAsync(Arg.Any<LLMConversation>());
    }
}
