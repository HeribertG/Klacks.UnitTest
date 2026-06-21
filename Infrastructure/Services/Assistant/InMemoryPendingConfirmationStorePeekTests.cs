// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InMemoryPendingConfirmationStore.PeekLatestForUser — verifies it surfaces a
/// user's outstanding confirmation token without consuming it, returns the most recent one,
/// is scoped to the user, and ignores users with no pending action. This is what lets the
/// orchestrator resurface the token on the confirmation turn (it is lost from chat history).
/// </summary>

using System.Collections.Generic;
using Klacks.Api.Infrastructure.Services.Assistant;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class InMemoryPendingConfirmationStorePeekTests
{
    private InMemoryPendingConfirmationStore _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new InMemoryPendingConfirmationStore();
    }

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private static Dictionary<string, object> Params() => new() { ["k"] = "v" };

    [Test]
    public void PeekLatestForUser_Returns_Null_When_No_Pending()
    {
        _sut.PeekLatestForUser(Guid.NewGuid(), Window).ShouldBeNull();
    }

    [Test]
    public void PeekLatestForUser_Returns_Token_And_Skill_For_User()
    {
        var user = Guid.NewGuid();
        var token = _sut.Create(user, "assign_user_permissions", Params());

        var handle = _sut.PeekLatestForUser(user, Window);

        handle.ShouldNotBeNull();
        handle!.Token.ShouldBe(token);
        handle.SkillName.ShouldBe("assign_user_permissions");
    }

    [Test]
    public void PeekLatestForUser_Does_Not_Consume()
    {
        var user = Guid.NewGuid();
        var token = _sut.Create(user, "delete_system_user", Params());

        _sut.PeekLatestForUser(user, Window).ShouldNotBeNull();

        // Still consumable after peeking — peek is non-destructive.
        _sut.Consume(token, user).ShouldNotBeNull();
    }

    [Test]
    public void PeekLatestForUser_Is_Scoped_To_User()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        _sut.Create(userA, "delete_system_user", Params());

        _sut.PeekLatestForUser(userB, Window).ShouldBeNull();
    }

    [Test]
    public void PeekLatestForUser_Returns_Most_Recent_When_Multiple()
    {
        var user = Guid.NewGuid();
        _sut.Create(user, "first_skill", Params());
        var secondToken = _sut.Create(user, "second_skill", Params());

        var handle = _sut.PeekLatestForUser(user, Window);

        handle.ShouldNotBeNull();
        handle!.Token.ShouldBe(secondToken);
        handle.SkillName.ShouldBe("second_skill");
    }

    [Test]
    public void PeekLatestForUser_Returns_Null_After_Consumption()
    {
        var user = Guid.NewGuid();
        var token = _sut.Create(user, "set_autonomy_level", Params());
        _sut.Consume(token, user);

        _sut.PeekLatestForUser(user, Window).ShouldBeNull();
    }

    [Test]
    public void PeekLatestForUser_Excludes_Pending_Older_Than_Window()
    {
        var user = Guid.NewGuid();
        _sut.Create(user, "delete_system_user", Params());

        // A freshly created pending is ~0s old; a negative window makes it "too old" to auto-force,
        // proving the recency bound excludes a stale/misdirected confirmation while the token itself
        // stays valid for an explicit confirm_pending_action call.
        _sut.PeekLatestForUser(user, TimeSpan.FromMilliseconds(-1)).ShouldBeNull();
    }
}
