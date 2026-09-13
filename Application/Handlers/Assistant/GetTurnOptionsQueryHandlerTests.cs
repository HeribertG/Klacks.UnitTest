// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetTurnOptionsQueryHandler: hashing of the raw message, the user-scoped trajectory
/// lookup, removal of always-on plumbing and of the skill the model chose, rank order, the option
/// cap, the description lookup and the behaviour on unparsable candidate json.
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class GetTurnOptionsQueryHandlerTests
{
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";
    private const string Message = "Lege einen neuen Kunden an";

    private ISkillSelectionTrajectoryRepository _trajectories = null!;
    private ISkillCacheService _skillCache = null!;
    private GetTurnOptionsQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _trajectories = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentSkill>)new List<AgentSkill>());
        _handler = new GetTurnOptionsQueryHandler(_trajectories, _skillCache);
    }

    private static SkillSelectionTrajectory MakeTrajectory(string userId, string candidatesJson, string? chosenSkill) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        UserId = userId,
        Locale = "de",
        UserMessageHash = MessageNormalizer.Hash(Message),
        IntentExcerpt = Message,
        KnowledgeIndexCandidatesJson = candidatesJson,
        LlmChosenSkill = chosenSkill,
        CreateTime = new DateTime(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc)
    };

    private void GivenTrajectory(SkillSelectionTrajectory trajectory) =>
        _trajectories.FindMostRecentByUserAndHashAsync(
                trajectory.UserId, MessageNormalizer.Hash(Message), Arg.Any<CancellationToken>())
            .Returns(trajectory);

    private Task<TurnOptionsResult> HandleAsync() =>
        _handler.Handle(new GetTurnOptionsQuery { UserId = UserId, UserMessage = Message }, CancellationToken.None);

    [Test]
    public async Task Handle_NoTrajectoryForTheHash_ReportsNotFoundWithNoOptions()
    {
        _trajectories.FindMostRecentByUserAndHashAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SkillSelectionTrajectory?)null);

        var result = await HandleAsync();

        result.Outcome.ShouldBe(TurnOptionsOutcome.NotFound);
        result.Options.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_TrajectoryOfAnotherUser_ReportsNotFoundAndLeaksNoOption()
    {
        GivenTrajectory(MakeTrajectory(
            OtherUserId,
            "[{\"name\":\"create_client\",\"rank\":1,\"score\":null,\"source\":\"Retrieved\"}]",
            "list_clients"));

        var result = await HandleAsync();

        result.Outcome.ShouldBe(TurnOptionsOutcome.NotFound);
        result.Options.ShouldBeEmpty();
        await _trajectories.Received(1).FindMostRecentByUserAndHashAsync(
            UserId, MessageNormalizer.Hash(Message), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_DropsAlwaysOnPlumbingAndTheSkillTheModelChose()
    {
        GivenTrajectory(MakeTrajectory(
            UserId,
            "[{\"name\":\"get_user_context\",\"rank\":1,\"score\":null,\"source\":\"AlwaysOn\"},"
            + "{\"name\":\"list_clients\",\"rank\":2,\"score\":0.7,\"source\":\"Retrieved\"},"
            + "{\"name\":\"create_client\",\"rank\":3,\"score\":0.6,\"source\":\"Retrieved\"}]",
            "list_clients"));

        var result = await HandleAsync();

        result.Outcome.ShouldBe(TurnOptionsOutcome.Found);
        result.Options.Select(option => option.SkillName).ShouldBe(new[] { "create_client" });
    }

    [Test]
    public async Task Handle_KeepsTheRankOrderAndCapsTheList()
    {
        var candidates = string.Join(",", Enumerable.Range(1, TurnOptionsDefaults.MaxOptions + 5)
            .Select(rank => $"{{\"name\":\"skill_{rank:D2}\",\"rank\":{rank},\"score\":null,\"source\":\"Retrieved\"}}"));
        GivenTrajectory(MakeTrajectory(UserId, "[" + candidates + "]", chosenSkill: null));

        var result = await HandleAsync();

        result.Options.Count.ShouldBe(TurnOptionsDefaults.MaxOptions);
        result.Options[0].SkillName.ShouldBe("skill_01");
        result.Options[^1].SkillName.ShouldBe($"skill_{TurnOptionsDefaults.MaxOptions:D2}");
    }

    [Test]
    public async Task Handle_AddsDisplayNameAndDescription()
    {
        _skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentSkill>)new List<AgentSkill>
            {
                new() { Name = "create_client", Description = "Creates a new client record." }
            });
        GivenTrajectory(MakeTrajectory(
            UserId,
            "[{\"name\":\"create_client\",\"rank\":1,\"score\":null,\"source\":\"Retrieved\"},"
            + "{\"name\":\"open_unknown_tool\",\"rank\":2,\"score\":null,\"source\":\"Hint\"}]",
            chosenSkill: null));

        var result = await HandleAsync();

        result.Options[0].DisplayName.ShouldBe("Create client");
        result.Options[0].Description.ShouldBe("Creates a new client record.");
        result.Options[1].DisplayName.ShouldBe("Open unknown tool");
        result.Options[1].Description.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Handle_UnparsableCandidateJson_ReportsFoundWithNoOptions()
    {
        GivenTrajectory(MakeTrajectory(UserId, "not json at all", chosenSkill: null));

        var result = await HandleAsync();

        result.Outcome.ShouldBe(TurnOptionsOutcome.Found);
        result.Options.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_WithoutAUserId_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(
            new GetTurnOptionsQuery { UserId = string.Empty, UserMessage = Message }, CancellationToken.None));
    }

    [Test]
    public async Task Handle_WithoutAUserMessage_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(
            new GetTurnOptionsQuery { UserId = UserId, UserMessage = string.Empty }, CancellationToken.None));
    }
}
