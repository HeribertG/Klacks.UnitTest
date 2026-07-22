// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for RecentEntityRegistrar: a successful tracked create skill persists a recent-entity row with
/// the extracted type/id/name/action for the acting user and conversation; an unlisted skill, an empty
/// acting user, and a missing conversation session all register nothing.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class RecentEntityRegistrarTests
{
    private IRecentEntityRepository _repository = null!;
    private RecentEntityRegistrar _sut = null!;
    private RecentEntityRow? _captured;

    private static readonly Guid UserId = Guid.NewGuid();
    private const string ConversationId = "conversation-1";

    [SetUp]
    public void SetUp()
    {
        _captured = null;
        _repository = Substitute.For<IRecentEntityRepository>();
        _repository.AddAsync(Arg.Do<RecentEntityRow>(r => _captured = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _sut = new RecentEntityRegistrar(_repository, NullLogger<RecentEntityRegistrar>.Instance);
    }

    private static SkillDescriptor Descriptor(string name) => new(
        name, "test", SkillCategory.Crud,
        Array.Empty<SkillParameter>(),
        Array.Empty<string>(),
        Array.Empty<LLMCapability>(),
        null);

    private static SkillExecutionContext Context(Guid userId, string? sessionId) => new()
    {
        UserId = userId,
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = Array.Empty<string>(),
        SessionId = sessionId
    };

    [Test]
    public async Task RegisterAsync_persists_row_for_tracked_create_skill()
    {
        var shiftId = Guid.NewGuid();
        var result = SkillResult.SuccessResult(new { ShiftId = shiftId, Name = "Frühdienst" });

        await _sut.RegisterAsync(Descriptor("create_shift"), Context(UserId, ConversationId), result);

        await _repository.Received(1).AddAsync(Arg.Any<RecentEntityRow>(), Arg.Any<CancellationToken>());
        _captured.ShouldNotBeNull();
        _captured!.UserId.ShouldBe(UserId);
        _captured.ConversationId.ShouldBe(ConversationId);
        _captured.EntityType.ShouldBe("shift");
        _captured.EntityId.ShouldBe(shiftId);
        _captured.DisplayName.ShouldBe("Frühdienst");
        _captured.Action.ShouldBe("created");
    }

    [Test]
    public async Task RegisterAsync_skips_unlisted_skill()
    {
        var result = SkillResult.SuccessResult(new { ClientId = Guid.NewGuid() });

        await _sut.RegisterAsync(Descriptor("add_break"), Context(UserId, ConversationId), result);

        await _repository.DidNotReceive().AddAsync(Arg.Any<RecentEntityRow>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterAsync_skips_when_user_is_empty()
    {
        var result = SkillResult.SuccessResult(new { ShiftId = Guid.NewGuid(), Name = "x" });

        await _sut.RegisterAsync(Descriptor("create_shift"), Context(Guid.Empty, ConversationId), result);

        await _repository.DidNotReceive().AddAsync(Arg.Any<RecentEntityRow>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RegisterAsync_skips_when_conversation_missing()
    {
        var result = SkillResult.SuccessResult(new { ShiftId = Guid.NewGuid(), Name = "x" });

        await _sut.RegisterAsync(Descriptor("create_shift"), Context(UserId, null), result);

        await _repository.DidNotReceive().AddAsync(Arg.Any<RecentEntityRow>(), Arg.Any<CancellationToken>());
    }
}
