// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W1.7: TrackUsageAsync must persist the serialized functions_called JSON on the llm_usage row. Follow-up 5:
/// the conversation row is only ever changed through the targeted repository calls (message count, title
/// proposal, token and cost increments), never by writing the loaded entity back.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMConversationManagerTests
{
    private ILLMRepository _repository = null!;
    private LLMConversationManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ILLMRepository>();
        _manager = new LLMConversationManager(
            Substitute.For<ILogger<LLMConversationManager>>(), _repository);
    }

    [Test]
    public async Task TrackUsageAsync_WritesFunctionsCalledJson()
    {
        LLMUsage? captured = null;
        await _repository.TrackUsageAsync(Arg.Do<LLMUsage>(u => captured = u));

        await _manager.TrackUsageAsync(
            "user-1",
            new LLMModel { Id = Guid.NewGuid(), ModelId = "deepseek-v4-pro" },
            new LLMConversation { ConversationId = Guid.NewGuid().ToString(), UserId = "user-1" },
            new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage(),
            900,
            functionsCalledJson: "[\"list_open_shifts\",\"cut_shift\"]");

        captured.ShouldNotBeNull();
        captured!.FunctionsCalled.ShouldBe("[\"list_open_shifts\",\"cut_shift\"]");
    }

    [Test]
    public async Task TrackUsageAsync_WithoutFunctionsCalled_LeavesColumnNull()
    {
        LLMUsage? captured = null;
        await _repository.TrackUsageAsync(Arg.Do<LLMUsage>(u => captured = u));

        await _manager.TrackUsageAsync(
            "user-1",
            new LLMModel { Id = Guid.NewGuid(), ModelId = "deepseek-v4-pro" },
            new LLMConversation { ConversationId = Guid.NewGuid().ToString(), UserId = "user-1" },
            new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage(),
            900);

        captured.ShouldNotBeNull();
        captured!.FunctionsCalled.ShouldBeNull();
    }

    // W1.9: the tool_choice measurement flags travel onto the usage row.
    [Test]
    public async Task TrackUsageAsync_WritesToolChoiceFlags()
    {
        LLMUsage? captured = null;
        await _repository.TrackUsageAsync(Arg.Do<LLMUsage>(u => captured = u));

        await _manager.TrackUsageAsync(
            "user-1",
            new LLMModel { Id = Guid.NewGuid(), ModelId = "deepseek-v4-pro" },
            new LLMConversation { ConversationId = Guid.NewGuid().ToString(), UserId = "user-1" },
            new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage(),
            900,
            toolChoiceRequested: true,
            toolChoiceSupported: true,
            toolCallReturned: true);

        captured.ShouldNotBeNull();
        captured!.ToolChoiceRequested.ShouldBeTrue();
        captured.ToolChoiceSupported.ShouldBeTrue();
        captured.ToolCallReturned.ShouldBeTrue();
    }

    [Test]
    public async Task TrackUsageAsync_WithoutToolChoiceFlags_DefaultsToFalse()
    {
        LLMUsage? captured = null;
        await _repository.TrackUsageAsync(Arg.Do<LLMUsage>(u => captured = u));

        await _manager.TrackUsageAsync(
            "user-1",
            new LLMModel { Id = Guid.NewGuid(), ModelId = "deepseek-v4-pro" },
            new LLMConversation { ConversationId = Guid.NewGuid().ToString(), UserId = "user-1" },
            new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage(),
            900);

        captured.ShouldNotBeNull();
        captured!.ToolChoiceRequested.ShouldBeFalse();
        captured.ToolChoiceSupported.ShouldBeFalse();
        captured.ToolCallReturned.ShouldBeFalse();
    }

    [Test]
    public async Task SaveConversationMessagesAsync_HandsTheTurnToTheRepositoryAsAnIncrementWithATitleProposal()
    {
        var conversation = new LLMConversation { ConversationId = "c-1", UserId = "user-1", MessageCount = 6 };

        await _manager.SaveConversationMessagesAsync(
            conversation, "one two three four five six", "answer", "model-1");

        await _repository.Received(1).RecordConversationTurnAsync(
            conversation, 2, Arg.Any<DateTime>(), "model-1", "one two three four five...");
        conversation.MessageCount.ShouldBe(6, "the row is incremented in SQL, not by writing the entity back");
    }

    [Test]
    public async Task SaveConversationMessagesAsync_ForALatePersistedTurn_HandsOverTheTurnStartAsTheMessageTime()
    {
        var conversation = new LLMConversation { ConversationId = "c-1", UserId = "user-1" };
        var turnStart = DateTime.UtcNow.AddMinutes(-3);

        await _manager.SaveConversationMessagesAsync(conversation, "hello", "answer", "model-1", turnStart);

        await _repository.Received(1).RecordConversationTurnAsync(
            conversation, 2, turnStart, "model-1", "hello");
    }

    [Test]
    public async Task TrackUsageAsync_AddsTheTurnTotalsThroughTheRepository()
    {
        var conversation = new LLMConversation { ConversationId = "c-1", UserId = "user-1" };
        var usage = new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage { InputTokens = 30, OutputTokens = 12, Cost = 0.5m };

        await _manager.TrackUsageAsync(
            "user-1", new LLMModel { Id = Guid.NewGuid(), ModelId = "m" }, conversation, usage, 900);

        await _repository.Received(1).AddConversationUsageAsync(conversation, 42, 0.5m);
    }

    [Test]
    public async Task TrackUsageAsync_ForAFailedTurn_AddsNoTotals()
    {
        var conversation = new LLMConversation { ConversationId = "c-1", UserId = "user-1" };
        var usage = new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage { InputTokens = 30, Cost = 0.5m };

        await _manager.TrackUsageAsync(
            "user-1", new LLMModel { Id = Guid.NewGuid(), ModelId = "m" }, conversation, usage, 900, hasError: true);

        await _repository.DidNotReceive().AddConversationUsageAsync(
            Arg.Any<LLMConversation>(), Arg.Any<int>(), Arg.Any<decimal>());
    }
}
