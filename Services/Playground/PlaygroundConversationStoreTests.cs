// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundConversationStore: history is kept per visitor and conversation in memory, capped at the
/// newest 20 messages, expires after its lifetime, is bounded in number, and a different visitor with the same
/// conversation id sees nothing.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Playground;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundConversationStoreTests
{
    private const string UserId = "demo-admin";

    private IPlaygroundMode _mode = null!;
    private SettableTimeProvider _clock = null!;
    private PlaygroundConversationStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _mode.IsActive.Returns(true);
        _clock = new SettableTimeProvider(new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc));
        _sut = new PlaygroundConversationStore(_mode, _clock);
        PlaygroundVisitorContext.Set(new PlaygroundVisitor("1.2.3.4", "device-a"));
    }

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    private void Turn(string conversationId, string user, string assistant)
    {
        var conversation = _sut.GetOrCreate(conversationId, UserId);
        _sut.AppendTurn(conversation, user, assistant, "deepseek-flash", _clock.Now, "title");
    }

    [Test]
    public void IsActive_FollowsThePlaygroundMode()
    {
        _sut.IsActive.ShouldBeTrue();
        _mode.IsActive.Returns(false);
        _sut.IsActive.ShouldBeFalse();
    }

    [Test]
    public void Turn_IsReadBackOldestFirstWithCounters()
    {
        Turn("c1", "first question", "first answer");
        Turn("c1", "second question", "second answer");

        var messages = _sut.GetMessages("c1", UserId);

        messages.Select(m => m.Content).ShouldBe(["first question", "first answer", "second question", "second answer"]);
        messages.Select(m => m.Role).ShouldBe(["user", "assistant", "user", "assistant"]);
        _sut.GetOrCreate("c1", UserId).MessageCount.ShouldBe(4);
        _sut.GetOrCreate("c1", UserId).Title.ShouldBe("title");
    }

    [Test]
    public void IsOngoing_OnlyAfterTheFirstTurn()
    {
        _sut.IsOngoing("c1", UserId).ShouldBeFalse();
        _sut.GetOrCreate("c1", UserId);
        _sut.IsOngoing("c1", UserId).ShouldBeFalse();

        Turn("c1", "q", "a");

        _sut.IsOngoing("c1", UserId).ShouldBeTrue();
    }

    [Test]
    public void History_KeepsOnlyTheNewestMessagesUpToTheLimit()
    {
        for (var i = 0; i < 15; i++)
        {
            Turn("c1", $"q{i}", $"a{i}");
        }

        var messages = _sut.GetMessages("c1", UserId);

        messages.Count.ShouldBe(AssistantLimitDefaults.ConversationMessagesLimit);
        messages[^1].Content.ShouldBe("a14");
        messages[0].Content.ShouldBe("q5");
    }

    [Test]
    public void AnotherVisitor_WithTheSameConversationId_SeesNothing()
    {
        Turn("shared-id", "private question", "private answer");

        PlaygroundVisitorContext.Set(new PlaygroundVisitor("5.6.7.8", "device-b"));

        _sut.GetMessages("shared-id", UserId).ShouldBeEmpty();
        _sut.IsOngoing("shared-id", UserId).ShouldBeFalse();
    }

    [Test]
    public void VisitorWithoutDeviceToken_IsKeyedByIpBucket()
    {
        PlaygroundVisitorContext.Set(new PlaygroundVisitor("9.9.9.9", null));
        Turn("c1", "q", "a");

        PlaygroundVisitorContext.Set(new PlaygroundVisitor("9.9.9.9", null));
        _sut.GetMessages("c1", UserId).Count.ShouldBe(2);

        PlaygroundVisitorContext.Set(new PlaygroundVisitor("8.8.8.8", null));
        _sut.GetMessages("c1", UserId).ShouldBeEmpty();
    }

    [Test]
    public void History_ExpiresAfterTheLifetimeWithoutUse()
    {
        Turn("c1", "q", "a");

        _clock.Now = _clock.Now.AddMinutes(PlaygroundHardeningConstants.ConversationTtlMinutes + 1);

        _sut.GetMessages("c1", UserId).ShouldBeEmpty();
        _sut.IsOngoing("c1", UserId).ShouldBeFalse();
    }

    [Test]
    public void History_LifetimeIsSlidingWhileTheConversationIsUsed()
    {
        Turn("c1", "q", "a");

        _clock.Now = _clock.Now.AddMinutes(PlaygroundHardeningConstants.ConversationTtlMinutes - 1);
        Turn("c1", "q2", "a2");
        _clock.Now = _clock.Now.AddMinutes(PlaygroundHardeningConstants.ConversationTtlMinutes - 1);

        _sut.GetMessages("c1", UserId).Count.ShouldBe(4);
    }

    [Test]
    public void StoredConversations_AreBoundedAndTheLeastRecentlyUsedGoesFirst()
    {
        for (var i = 0; i < PlaygroundHardeningConstants.MaxStoredConversations + 5; i++)
        {
            _clock.Now = _clock.Now.AddSeconds(1);
            Turn($"c{i}", "q", "a");
        }

        _sut.GetMessages("c0", UserId).ShouldBeEmpty();
        _sut.GetMessages($"c{PlaygroundHardeningConstants.MaxStoredConversations + 4}", UserId).Count.ShouldBe(2);
    }

    [Test]
    public void ConversationOfAnotherOwner_IsSeparate()
    {
        Turn("c1", "q", "a");

        _sut.GetMessages("c1", "other-user").ShouldBeEmpty();
    }
}
