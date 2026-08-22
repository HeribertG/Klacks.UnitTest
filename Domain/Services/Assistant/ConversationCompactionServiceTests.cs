// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ConversationCompactionService: a valid structured JSON model response is stored
/// as structured JSON, a non-JSON response falls back to truncated free text, an existing legacy
/// free-text summary is migrated into the structured facts on the next run, and the compaction is
/// skipped below the message-count threshold or when there are no old messages to summarize. Also
/// covers the parametrized CompactIfNeededAsync(conversationId, userId, minMessages) overload used by
/// the task-boundary trigger, which must respect its own threshold independently of the default one,
/// and that compaction never touches a conversation owned by someone else.
/// </summary>

using Microsoft.Extensions.Logging;
using LlmProviders = Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ConversationCompactionServiceTests
{
    private const string ConvId = "conv-1";
    private const string OwnerId = "owner-1";
    private const string OtherUserId = "other-user";
    private const int ReadyMessageCount = 40;
    private const string CheapModelId = "m-1";

    private const string StructuredResponse =
        "{\"openTasks\":[\"Finish the roster\"],\"decisions\":[\"Use the night shift\"],\"facts\":[\"Prefers mornings\"]}";

    private ILLMRepository _repository = null!;
    private ICheapestModelResolver _cheapestModelResolver = null!;
    private LlmProviders.ILLMProvider _provider = null!;
    private ConversationCompactionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _cheapestModelResolver = Substitute.For<ICheapestModelResolver>();
        _provider = Substitute.For<LlmProviders.ILLMProvider>();
        _service = new ConversationCompactionService(
            Substitute.For<ILogger<ConversationCompactionService>>(),
            _cheapestModelResolver,
            _repository);
    }

    private LLMConversation ArrangeReadyConversation(string? existingSummary, int messageCount = ReadyMessageCount)
    {
        var conversation = new LLMConversation
        {
            ConversationId = ConvId,
            UserId = OwnerId,
            MessageCount = messageCount,
            Summary = existingSummary
        };

        _repository.GetConversationByConversationIdAsync(ConvId, OwnerId).Returns(conversation);
        _repository.GetOldestMessagesAsync(ConvId, OwnerId, Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<LLMMessage>
            {
                new() { Role = "user", Content = "How do I plan the week?" },
                new() { Role = "assistant", Content = "Let us start with the roster." }
            });
        var cheapModel = new LLMModel
        {
            ModelId = CheapModelId,
            ApiModelId = "api-m-1",
            CostPerInputToken = 0.1m,
            CostPerOutputToken = 0.1m
        };
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)cheapModel, (LlmProviders.ILLMProvider?)_provider));

        return conversation;
    }

    private void ArrangeProviderResponse(string content)
    {
        _provider.ProcessAsync(Arg.Any<LlmProviders.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LlmProviders.LLMProviderResponse { Success = true, Content = content });
    }

    [Test]
    public async Task StructuredResponse_StoredAsStructuredJson()
    {
        var conversation = ArrangeReadyConversation(existingSummary: null);
        ArrangeProviderResponse(StructuredResponse);

        await _service.CompactIfNeededAsync(ConvId, OwnerId);

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

        await _service.CompactIfNeededAsync(ConvId, OwnerId);

        conversation.Summary.ShouldBe(plain);
        ConversationSummaryCodec.TryParse(conversation.Summary, out _).ShouldBeFalse();
    }

    [Test]
    public async Task LegacyFreeTextSummary_MigratedIntoFacts()
    {
        const string legacy = "The user is a nurse in Bern and works night shifts.";
        var conversation = ArrangeReadyConversation(existingSummary: legacy);
        ArrangeProviderResponse(StructuredResponse);

        await _service.CompactIfNeededAsync(ConvId, OwnerId);

        ConversationSummaryCodec.TryParse(conversation.Summary, out var stored).ShouldBeTrue();
        stored.Facts.ShouldContain(legacy);
        stored.Facts.ShouldContain("Prefers mornings");
    }

    [Test]
    public async Task BelowThreshold_DoesNothing()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old", messageCount: 5);

        await _service.CompactIfNeededAsync(ConvId, OwnerId);

        conversation.Summary.ShouldBe("old");
        await _provider.DidNotReceive()
            .ProcessAsync(Arg.Any<LlmProviders.LLMProviderRequest>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().UpdateConversationAsync(Arg.Any<LLMConversation>());
    }

    [Test]
    public async Task NoOldMessages_DoesNothing()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old");
        _repository.GetOldestMessagesAsync(ConvId, OwnerId, Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<LLMMessage>());

        await _service.CompactIfNeededAsync(ConvId, OwnerId);

        conversation.Summary.ShouldBe("old");
        await _repository.DidNotReceive().UpdateConversationAsync(Arg.Any<LLMConversation>());
    }

    [Test]
    public async Task ForeignUser_DoesNotCompactAnotherUsersConversation()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old");
        ArrangeProviderResponse(StructuredResponse);

        await _service.CompactIfNeededAsync(ConvId, OtherUserId);

        conversation.Summary.ShouldBe("old");
        await _repository.DidNotReceive().UpdateConversationAsync(Arg.Any<LLMConversation>());
        await _repository.DidNotReceive()
            .GetOldestMessagesAsync(ConvId, OtherUserId, Arg.Any<int>(), Arg.Any<int>());
    }

    [Test]
    public async Task MissingOwner_IsRejectedWithoutTouchingTheRepository()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old");

        await _service.CompactIfNeededAsync(ConvId, string.Empty);

        conversation.Summary.ShouldBe("old");
        await _repository.DidNotReceive()
            .GetConversationByConversationIdAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task ParametrizedOverload_BelowGivenMinMessages_DoesNothing()
    {
        var conversation = ArrangeReadyConversation(existingSummary: "old", messageCount: 8);

        await _service.CompactIfNeededAsync(ConvId, OwnerId, minMessages: 10);

        conversation.Summary.ShouldBe("old");
        await _provider.DidNotReceive()
            .ProcessAsync(Arg.Any<LlmProviders.LLMProviderRequest>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().UpdateConversationAsync(Arg.Any<LLMConversation>());
    }

    [Test]
    public async Task ParametrizedOverload_AtOrAboveGivenMinMessages_ProceedsEvenBelowDefaultThreshold()
    {
        var conversation = ArrangeReadyConversation(existingSummary: null, messageCount: 10);
        ArrangeProviderResponse(StructuredResponse);

        await _service.CompactIfNeededAsync(ConvId, OwnerId, minMessages: 10);

        ConversationSummaryCodec.TryParse(conversation.Summary, out var stored).ShouldBeTrue();
        stored.Facts.ShouldContain("Prefers mornings");
        await _repository.Received(1).UpdateConversationAsync(conversation);
    }
}
