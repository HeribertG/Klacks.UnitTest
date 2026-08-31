// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// W1.7: TrackUsageAsync must persist the serialized functions_called JSON on the llm_usage row.
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
}
