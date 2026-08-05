// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SubmitCorrectionCommandHandler hash lookup, validation and trajectory update.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant;

using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SubmitCorrectionCommandHandlerTests
{
    private const int HashPrefixLength = 16;

    private ISkillSelectionTrajectoryRepository _repository = null!;
    private ILLMBackgroundTaskService _backgroundTasks = null!;
    private ILogger<SubmitCorrectionCommandHandler> _logger = null!;
    private IAgentMemoryRepository _agentMemories = null!;
    private SubmitCorrectionCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _backgroundTasks = Substitute.For<ILLMBackgroundTaskService>();
        _logger = Substitute.For<ILogger<SubmitCorrectionCommandHandler>>();
        _agentMemories = Substitute.For<IAgentMemoryRepository>();
        _agentMemories.GetByCategoryAndKeysAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory>());
        _handler = new SubmitCorrectionCommandHandler(_repository, _backgroundTasks, _agentMemories, _logger);
    }

    [Test]
    public async Task Handle_TrajectoryFound_FlagsAsCorrected()
    {
        const string userId = "user-1";
        const string message = "Lösche Mitarbeiter Max";
        var hash = ExpectedHashPrefix(message);

        var existing = new SkillSelectionTrajectory { Id = Guid.NewGuid(), UserId = userId, UserMessageHash = hash };
        _repository.FindMostRecentByUserAndHashAsync(userId, hash, Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = userId,
            UserMessage = message,
            CorrectionType = CorrectionTypes.WrongSkill
        }, CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.TrajectoryId.ShouldBe(existing.Id);
        existing.WasCorrected.ShouldBeTrue();
        existing.CorrectionType.ShouldBe(CorrectionTypes.WrongSkill);
        await _repository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_WrongSkillCorrection_TriggersAReflectionScopedToTheChosenSkill()
    {
        const string userId = "user-1";
        const string message = "Lösche Mitarbeiter Max";
        var agentId = Guid.NewGuid();
        var existing = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UserId = userId,
            UserMessageHash = ExpectedHashPrefix(message),
            LlmChosenSkill = "delete_client"
        };
        _repository.FindMostRecentByUserAndHashAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = userId,
            UserMessage = message,
            CorrectionType = CorrectionTypes.WrongSkill
        }, CancellationToken.None);

        _backgroundTasks.Received(1).TriggerReflection(Arg.Is<TurnReflectionRequest>(r =>
            r.AgentId == agentId &&
            r.Trigger == ReflectionTriggers.UserCorrection &&
            r.ScopeKey == "delete_client"));
    }

    [Test]
    public async Task Handle_NoneNeededCorrection_TriggersNoReflection()
    {
        const string userId = "user-1";
        const string message = "Lösche Mitarbeiter Max";
        var existing = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = userId,
            UserMessageHash = ExpectedHashPrefix(message),
            LlmChosenSkill = "delete_client"
        };
        _repository.FindMostRecentByUserAndHashAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = userId,
            UserMessage = message,
            CorrectionType = CorrectionTypes.NoneNeeded
        }, CancellationToken.None);

        // NoneNeeded says the turn was fine after all; drawing a lesson from it would teach a mistake
        // that never happened.
        _backgroundTasks.DidNotReceive().TriggerReflection(Arg.Any<TurnReflectionRequest>());
    }

    [Test]
    public async Task Handle_NoneNeededCorrection_RevokesTheLatestUncoveredClaimLesson()
    {
        const string userId = "user-1";
        const string message = "Lösche Mitarbeiter Max";
        var agentId = Guid.NewGuid();
        var existing = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UserId = userId,
            UserMessageHash = ExpectedHashPrefix(message),
            LlmChosenSkill = "delete_client"
        };
        _repository.FindMostRecentByUserAndHashAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existing);
        var older = new AgentMemory { Id = Guid.NewGuid(), Key = "delete_client", SourceRef = ReflectionTriggers.UncoveredClaim, CreateTime = DateTime.UtcNow.AddDays(-2) };
        var latest = new AgentMemory { Id = Guid.NewGuid(), Key = "delete_client", SourceRef = ReflectionTriggers.UncoveredClaim, CreateTime = DateTime.UtcNow.AddHours(-1) };
        var foreignTrigger = new AgentMemory { Id = Guid.NewGuid(), Key = "delete_client", SourceRef = ReflectionTriggers.SkillFailure, CreateTime = DateTime.UtcNow };
        _agentMemories.GetByCategoryAndKeysAsync(
                agentId, Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentMemory> { older, latest, foreignTrigger });

        await _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = userId,
            UserMessage = message,
            CorrectionType = CorrectionTypes.NoneNeeded
        }, CancellationToken.None);

        // The user contradicted the coverage verdict for this skill; only the newest uncovered-claim
        // lesson goes, skill-failure lessons stay untouched.
        await _agentMemories.Received(1).DeleteAsync(latest.Id, Arg.Any<CancellationToken>());
        await _agentMemories.DidNotReceive().DeleteAsync(older.Id, Arg.Any<CancellationToken>());
        await _agentMemories.DidNotReceive().DeleteAsync(foreignTrigger.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CorrectionWithoutAChosenSkill_TriggersNoReflection()
    {
        const string userId = "user-1";
        const string message = "Lösche Mitarbeiter Max";
        var existing = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            UserId = userId,
            UserMessageHash = ExpectedHashPrefix(message),
            LlmChosenSkill = null
        };
        _repository.FindMostRecentByUserAndHashAsync(userId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        await _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = userId,
            UserMessage = message,
            CorrectionType = CorrectionTypes.WrongSkill
        }, CancellationToken.None);

        // Without a subject the lesson would have no scope and would apply everywhere.
        _backgroundTasks.DidNotReceive().TriggerReflection(Arg.Any<TurnReflectionRequest>());
    }

    [Test]
    public async Task Handle_TrajectoryMissing_ReturnsNotFoundWithoutUpdate()
    {
        _repository.FindMostRecentByUserAndHashAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SkillSelectionTrajectory?)null);

        var result = await _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = "user-1",
            UserMessage = "Anything",
            CorrectionType = CorrectionTypes.WrongParam
        }, CancellationToken.None);

        result.Found.ShouldBeFalse();
        result.TrajectoryId.ShouldBeNull();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<SkillSelectionTrajectory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Handle_UnknownCorrectionType_ThrowsArgumentException()
    {
        var act = () => _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = "user-1",
            UserMessage = "Hello",
            CorrectionType = "garbage"
        }, CancellationToken.None);

        Should.Throw<ArgumentException>(act).Message.ShouldContain("Unknown correction type");
    }

    [Test]
    public void Handle_MissingUserId_ThrowsArgumentException()
    {
        var act = () => _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = string.Empty,
            UserMessage = "Hello",
            CorrectionType = CorrectionTypes.WrongSkill
        }, CancellationToken.None);

        Should.Throw<ArgumentException>(act).Message.ShouldContain("UserId");
    }

    [Test]
    public void Handle_MissingMessage_ThrowsArgumentException()
    {
        var act = () => _handler.Handle(new SubmitCorrectionCommand
        {
            UserId = "user-1",
            UserMessage = "",
            CorrectionType = CorrectionTypes.WrongSkill
        }, CancellationToken.None);

        Should.Throw<ArgumentException>(act).Message.ShouldContain("UserMessage");
    }

    private static string ExpectedHashPrefix(string message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(bytes)[..HashPrefixLength];
    }
}
