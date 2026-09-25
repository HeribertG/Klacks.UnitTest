// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetTurnOptionsQueryHandler: hashing of the raw message, the user-scoped trajectory
/// lookup, removal of always-on plumbing - by recorded provenance and by the skill's own flag - and of
/// the skill the model chose, rank order, the option cap, the description lookup, the logged miss and
/// the behaviour on unparsable candidate json.
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
using Microsoft.Extensions.Logging;
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
    private ILogger<GetTurnOptionsQueryHandler> _logger = null!;
    private GetTurnOptionsQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _trajectories = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _skillCache = Substitute.For<ISkillCacheService>();
        _skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentSkill>)new List<AgentSkill>());
        _logger = Substitute.For<ILogger<GetTurnOptionsQueryHandler>>();
        _handler = new GetTurnOptionsQueryHandler(_trajectories, _skillCache, _logger);
    }

    private void GivenSkills(params AgentSkill[] skills) =>
        _skillCache.GetAllEnabledSkillsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<AgentSkill>)skills.ToList());

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

    private static string CandidatesOf(string skill) =>
        "[{\"name\":\"" + skill + "\",\"rank\":1,\"score\":0.7,\"source\":\"Retrieved\"}]";

    // Stop and resend of the same text leaves two trajectories under one hash and the stopped one is often
    // persisted last, so "most recent by hash" can show the options of the wrong twin.
    [Test]
    public async Task Handle_WithATurnId_ShowsTheOptionsOfExactlyThatTurnEvenWhenTheHashIsAmbiguous()
    {
        var turnId = Guid.NewGuid();
        var stopped = MakeTrajectory(UserId, CandidatesOf("create_client"), chosenSkill: null);
        stopped.TurnId = turnId;
        var resent = MakeTrajectory(UserId, CandidatesOf("list_clients"), chosenSkill: null);
        GivenTrajectory(resent);
        _trajectories.FindByUserAndTurnIdAsync(UserId, turnId, Arg.Any<CancellationToken>()).Returns(stopped);

        var result = await _handler.Handle(
            new GetTurnOptionsQuery { UserId = UserId, UserMessage = Message, TurnId = turnId }, CancellationToken.None);

        result.Outcome.ShouldBe(TurnOptionsOutcome.Found);
        result.Options.Select(option => option.SkillName).ShouldBe(new[] { "create_client" });
        await _trajectories.DidNotReceiveWithAnyArgs().FindMostRecentByUserAndHashAsync(default!, default!, default);
    }

    [Test]
    public async Task Handle_WithoutATurnId_KeepsTheHashLookup()
    {
        GivenTrajectory(MakeTrajectory(UserId, CandidatesOf("create_client"), chosenSkill: null));

        var result = await HandleAsync();

        result.Options.Select(option => option.SkillName).ShouldBe(new[] { "create_client" });
        await _trajectories.DidNotReceiveWithAnyArgs().FindByUserAndTurnIdAsync(default!, default, default);
    }

    [Test]
    public async Task Handle_WithATurnIdOfAnotherUser_ReportsNotFoundAndLeaksNoOption()
    {
        var turnId = Guid.NewGuid();
        _trajectories.FindByUserAndTurnIdAsync(UserId, turnId, Arg.Any<CancellationToken>())
            .Returns((SkillSelectionTrajectory?)null);

        var result = await _handler.Handle(
            new GetTurnOptionsQuery { UserId = UserId, UserMessage = Message, TurnId = turnId }, CancellationToken.None);

        result.Outcome.ShouldBe(TurnOptionsOutcome.NotFound);
        result.Options.ShouldBeEmpty();
        await _trajectories.Received(1).FindByUserAndTurnIdAsync(UserId, turnId, Arg.Any<CancellationToken>());
    }

    // An id that resolves to nothing must not fall back to the hash: the hash would name the older twin and
    // show its options in the menu of a turn it does not belong to.
    [Test]
    public async Task Handle_WithAnUnknownTurnId_DoesNotFallBackToTheHash()
    {
        GivenTrajectory(MakeTrajectory(UserId, CandidatesOf("list_clients"), chosenSkill: null));
        _trajectories.FindByUserAndTurnIdAsync(UserId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((SkillSelectionTrajectory?)null);

        var result = await _handler.Handle(
            new GetTurnOptionsQuery { UserId = UserId, UserMessage = Message, TurnId = Guid.NewGuid() },
            CancellationToken.None);

        result.Outcome.ShouldBe(TurnOptionsOutcome.NotFound);
        result.Options.ShouldBeEmpty();
        await _trajectories.DidNotReceiveWithAnyArgs().FindMostRecentByUserAndHashAsync(default!, default!, default);
    }

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

    // The provenance is a string on a telemetry row and was only added with the capture of W1.6: a turn
    // recorded before that names no source at all. Without the structural check such a candidate carries
    // plumbing into the menu, and an expected_skill naming it would send the sharpener after a tool no
    // user ever wants.
    [Test]
    public async Task Handle_DropsAnAlwaysOnSkillWhoseCandidateNamesNoSource()
    {
        GivenSkills(
            new AgentSkill { Name = "get_user_context", Description = "Reads the current context.", AlwaysOn = true },
            new AgentSkill { Name = "create_client", Description = "Creates a new client record." });
        GivenTrajectory(MakeTrajectory(
            UserId,
            "[{\"name\":\"get_user_context\",\"rank\":1},"
            + "{\"name\":\"create_client\",\"rank\":2,\"score\":0.6,\"source\":\"Retrieved\"}]",
            chosenSkill: null));

        var result = await HandleAsync();

        result.Outcome.ShouldBe(TurnOptionsOutcome.Found);
        result.Options.Select(option => option.SkillName).ShouldBe(new[] { "create_client" });
    }

    // The cache holds the enabled skills of every agent, so one name can arrive more than once. A
    // last-one-wins entry would let plumbing back into the menu as soon as a second agent carries the
    // same skill without the flag.
    [Test]
    public async Task Handle_DropsAnAlwaysOnSkillEvenWhenAnotherAgentCarriesItWithoutTheFlag()
    {
        GivenSkills(
            new AgentSkill { Name = "get_user_context", Description = "Reads the current context.", AlwaysOn = true },
            new AgentSkill { Name = "get_user_context", Description = "Reads the current context." });
        GivenTrajectory(MakeTrajectory(
            UserId, "[{\"name\":\"get_user_context\",\"rank\":1}]", chosenSkill: null));

        var result = await HandleAsync();

        result.Options.ShouldBeEmpty();
    }

    // The outcome never reaches the wire, so without a log line the difference between "no turn of this
    // caller was captured" and "the captured turn offers nothing" leaves no trace at all, and a menu
    // that stays empty cannot be told apart from one that was never found.
    [Test]
    public async Task Handle_NoTrajectoryForTheHash_IsLoggedAtInformation()
    {
        _trajectories.FindMostRecentByUserAndHashAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SkillSelectionTrajectory?)null);

        await HandleAsync();

        _logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
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
        GivenSkills(new AgentSkill { Name = "create_client", Description = "Creates a new client record." });
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
