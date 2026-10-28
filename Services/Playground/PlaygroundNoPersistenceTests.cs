// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that Playground visitors leave no text in the database: with the mode on the conversation manager keeps
/// the chat history in the in-memory store (multi-turn still works), never touches the conversation or message
/// tables and still records the cost row; the background-task and navigation-feedback decorators swallow every call.
/// With the mode off the repository is used exactly as before and every call is forwarded.
/// </summary>

using Klacks.Api.Application.Interfaces.Klacksy;
using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Playground;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using LLMFunctionCall = Klacks.Api.Domain.Services.Assistant.Providers.LLMFunctionCall;
using ProviderUsage = Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundNoPersistenceTests
{
    private const string UserId = "demo-admin";
    private const string ConversationId = "conv-1";

    private IPlaygroundMode _mode = null!;
    private ILLMRepository _repository = null!;
    private PlaygroundConversationStore _store = null!;
    private LLMConversationManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _repository = Substitute.For<ILLMRepository>();
        _store = new PlaygroundConversationStore(
            _mode, new SettableTimeProvider(new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc)));
        _manager = new LLMConversationManager(Substitute.For<ILogger<LLMConversationManager>>(), _repository, _store);
        PlaygroundVisitorContext.Set(new PlaygroundVisitor("1.2.3.4", "device-a"));
    }

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    private async Task SaveTurn(LLMConversation conversation, string user, string assistant) =>
        await _manager.SaveConversationMessagesAsync(conversation, user, assistant, "deepseek-flash");

    [Test]
    public async Task ModeOn_MultiTurnWorksFromMemoryAndNothingReachesTheConversationTables()
    {
        _mode.IsActive.Returns(true);

        var conversation = await _manager.GetOrCreateConversationAsync(ConversationId, UserId);
        await SaveTurn(conversation, "Wie viele Mitarbeiter?", "Es sind 12.");
        var history = await _manager.GetConversationHistoryAsync(ConversationId, UserId);

        history.Select(m => m.Content).ShouldBe(["Wie viele Mitarbeiter?", "Es sind 12."]);
        await _repository.DidNotReceiveWithAnyArgs().GetOrCreateConversationAsync(default!, default!);
        await _repository.DidNotReceiveWithAnyArgs().SaveMessageAsync(default!);
        await _repository.DidNotReceiveWithAnyArgs().RecordConversationTurnAsync(default!, default, default, default!, default);
        await _repository.DidNotReceiveWithAnyArgs().GetConversationMessagesAsync(default!, default!);
    }

    [Test]
    public async Task ModeOn_SecondTurnSeesTheFirstOne()
    {
        _mode.IsActive.Returns(true);
        var conversation = await _manager.GetOrCreateConversationAsync(ConversationId, UserId);
        await SaveTurn(conversation, "one", "uno");

        var again = await _manager.GetOrCreateConversationAsync(ConversationId, UserId);
        await SaveTurn(again, "two", "dos");

        (await _manager.GetConversationHistoryAsync(ConversationId, UserId)).Count.ShouldBe(4);
        again.ShouldBeSameAs(conversation);
        again.MessageCount.ShouldBe(4);
    }

    [Test]
    public async Task ModeOn_UsageRowIsStillRecordedButTheConversationRowIsNotTouched()
    {
        _mode.IsActive.Returns(true);
        var conversation = await _manager.GetOrCreateConversationAsync(ConversationId, UserId);

        await _manager.TrackUsageAsync(UserId, new LLMModel { Id = Guid.NewGuid() }, conversation, new ProviderUsage { InputTokens = 5, OutputTokens = 7 }, 120);

        await _repository.Received(1).TrackUsageAsync(Arg.Is<LLMUsage>(u => u.ConversationId == ConversationId));
        await _repository.DidNotReceiveWithAnyArgs().AddConversationUsageAsync(default!, default, default);
    }

    [Test]
    public async Task ModeOff_UsesTheRepositoryExactlyAsBefore()
    {
        _mode.IsActive.Returns(false);
        var stored = new LLMConversation { Id = Guid.NewGuid(), ConversationId = ConversationId, UserId = UserId };
        _repository.GetOrCreateConversationAsync(ConversationId, UserId).Returns(stored);
        _repository.GetConversationMessagesAsync(ConversationId, UserId).Returns(new List<LLMMessage>
        {
            new() { Role = "user", Content = "from db" },
        });

        var conversation = await _manager.GetOrCreateConversationAsync(ConversationId, UserId);
        await SaveTurn(conversation, "q", "a");
        var history = await _manager.GetConversationHistoryAsync(ConversationId, UserId);
        await _manager.TrackUsageAsync(UserId, new LLMModel { Id = Guid.NewGuid() }, conversation, new ProviderUsage { InputTokens = 1 }, 1);

        conversation.ShouldBeSameAs(stored);
        history.Single().Content.ShouldBe("from db");
        await _repository.Received(2).SaveMessageAsync(Arg.Any<LLMMessage>());
        await _repository.Received(1).RecordConversationTurnAsync(stored, 2, Arg.Any<DateTime>(), "deepseek-flash", Arg.Any<string?>());
        await _repository.Received(1).AddConversationUsageAsync(stored, Arg.Any<int>(), Arg.Any<decimal>());
        _store.IsOngoing(ConversationId, UserId).ShouldBeFalse();
    }

    [Test]
    public void BackgroundTasks_ModeOn_SwallowEveryHook()
    {
        _mode.IsActive.Returns(true);
        var inner = Substitute.For<ILLMBackgroundTaskService>();
        var sut = new PlaygroundSilentBackgroundTaskService(inner, _mode);
        var conversation = new LLMConversation { ConversationId = ConversationId, UserId = UserId };

        sut.RunBackgroundTasks(null, conversation, new LLMContext(), "answer", new List<LLMFunctionCall>());
        sut.RunStoppedTurnTasks(null, conversation, new LLMContext(), "answer", new List<LLMFunctionCall>(), "phase");
        sut.TriggerConversationCompaction(ConversationId, UserId, 30);
        sut.TriggerReflection(new TurnReflectionRequest(Guid.NewGuid(), "trigger", "message", "evidence", "scope", null));

        inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void BackgroundTasks_ModeOff_ForwardEveryHook()
    {
        _mode.IsActive.Returns(false);
        var inner = Substitute.For<ILLMBackgroundTaskService>();
        var sut = new PlaygroundSilentBackgroundTaskService(inner, _mode);
        var conversation = new LLMConversation { ConversationId = ConversationId, UserId = UserId };
        var context = new LLMContext();
        var calls = new List<LLMFunctionCall>();
        var reflection = new TurnReflectionRequest(Guid.NewGuid(), "trigger", "message", "evidence", "scope", null);

        sut.RunBackgroundTasks(null, conversation, context, "answer", calls, true);
        sut.RunStoppedTurnTasks(null, conversation, context, "answer", calls, "phase");
        sut.TriggerConversationCompaction(ConversationId, UserId, 30);
        sut.TriggerReflection(reflection);

        inner.Received(1).RunBackgroundTasks(null, conversation, context, "answer", calls, true);
        inner.Received(1).RunStoppedTurnTasks(null, conversation, context, "answer", calls, "phase");
        inner.Received(1).TriggerConversationCompaction(ConversationId, UserId, 30);
        inner.Received(1).TriggerReflection(reflection);
    }

    [Test]
    public async Task NavigationFeedback_ModeOn_StoresNothing()
    {
        _mode.IsActive.Returns(true);
        var inner = Substitute.For<INavigationFeedbackLogger>();
        var sut = new PlaygroundSilentNavigationFeedbackLogger(inner, _mode);

        await sut.LogAsync("open the schedule", "en", "target", 0.9, "/schedule", null, CancellationToken.None);
        await sut.LogOutcomeAsync("open the schedule", "en", "target", "accepted", "/schedule", null, CancellationToken.None);

        inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task NavigationFeedback_ModeOff_ForwardsBothCalls()
    {
        _mode.IsActive.Returns(false);
        var inner = Substitute.For<INavigationFeedbackLogger>();
        var sut = new PlaygroundSilentNavigationFeedbackLogger(inner, _mode);

        await sut.LogAsync("open the schedule", "en", "target", 0.9, "/schedule", null, CancellationToken.None);
        await sut.LogOutcomeAsync("open the schedule", "en", "target", "accepted", "/schedule", null, CancellationToken.None);

        await inner.Received(1).LogAsync("open the schedule", "en", "target", 0.9, "/schedule", null, Arg.Any<CancellationToken>());
        await inner.Received(1).LogOutcomeAsync("open the schedule", "en", "target", "accepted", "/schedule", null, Arg.Any<CancellationToken>());
    }
}
