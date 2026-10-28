// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests the Playground decorator of the previous-action store: with the mode on nothing is saved, marked or read (so the
/// visitor's last message never reaches the database and a correction turn finds no anchor); with the mode off every call
/// is forwarded to the real store unchanged.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundSilentAssistantLastActionStoreTests
{
    private IAssistantLastActionStore _inner = null!;
    private IPlaygroundMode _mode = null!;
    private PlaygroundSilentAssistantLastActionStore _sut = null!;
    private readonly Guid _userId = Guid.NewGuid();
    private const string ConversationId = "conversation-1";

    [SetUp]
    public void SetUp()
    {
        _inner = Substitute.For<IAssistantLastActionStore>();
        _inner.Peek(_userId, ConversationId).Returns(new AssistantLastAction { UserId = _userId, ConversationId = ConversationId });
        _mode = Substitute.For<IPlaygroundMode>();
        _sut = new PlaygroundSilentAssistantLastActionStore(_inner, _mode);
    }

    [Test]
    public void ModeOn_NothingIsSavedMarkedOrRead()
    {
        _mode.IsActive.Returns(true);

        _sut.Save(new AssistantLastAction { UserId = _userId, ConversationId = ConversationId, UserMessage = "KIWI-4711" });
        _sut.MarkSuperseded(_userId, ConversationId);
        _sut.SaveClarificationCandidates(_userId, ConversationId, ["a", "b"]);
        var peeked = _sut.Peek(_userId, ConversationId);

        peeked.ShouldBeNull();
        _inner.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void ModeOff_EveryCallIsForwarded()
    {
        _mode.IsActive.Returns(false);
        var action = new AssistantLastAction { UserId = _userId, ConversationId = ConversationId };

        _sut.Save(action);
        _sut.MarkSuperseded(_userId, ConversationId);
        _sut.SaveClarificationCandidates(_userId, ConversationId, ["a", "b"]);
        var peeked = _sut.Peek(_userId, ConversationId);

        peeked.ShouldNotBeNull();
        _inner.Received(1).Save(action);
        _inner.Received(1).MarkSuperseded(_userId, ConversationId);
        _inner.Received(1).SaveClarificationCandidates(_userId, ConversationId, Arg.Any<IReadOnlyList<string>>());
    }
}
