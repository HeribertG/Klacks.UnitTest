// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the thumbs-up handler. It is the only positive signal the usefulness oracle has, so the two
/// things that must hold are that it finds the same turn the correction endpoint would find - through
/// MessageNormalizer, not through the raw text - and that it can never turn into a negative statement
/// about somebody's turn.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant;

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SubmitHelpfulFeedbackCommandHandlerTests
{
    private const string UserId = "user-42";
    private const string Message = "Zeig mir die offenen Dienste";

    private ISkillSelectionTrajectoryRepository _repository = null!;
    private SubmitHelpfulFeedbackCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _handler = new SubmitHelpfulFeedbackCommandHandler(
            _repository, Substitute.For<ILogger<SubmitHelpfulFeedbackCommandHandler>>());
    }

    [Test]
    public async Task AKnownTurn_IsMarkedHelpful()
    {
        var trajectory = GivenTrajectory();

        var result = await _handler.Handle(Command(Message), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.TrajectoryId.ShouldBe(trajectory.Id);
        trajectory.Helpful.ShouldBe(true);
        await _repository.Received(1).UpdateAsync(trajectory, Arg.Any<CancellationToken>());
    }

    // The same normalisation the correction endpoint uses, which is the whole reason MessageNormalizer
    // exists: praise and complaint must never disagree about which turn was meant.
    [Test]
    public async Task TheLookup_UsesTheNormalisedHashSoCasingAndWhitespaceDoNotMatter()
    {
        GivenTrajectory();

        await _handler.Handle(Command("   ZEIG MIR   die offenen Dienste  "), CancellationToken.None);

        await _repository.Received(1).FindMostRecentByUserAndHashAsync(
            UserId, MessageNormalizer.Hash(Message), Arg.Any<CancellationToken>());
    }

    // Trajectory capture is fire-and-forget and may simply have lost the turn. The client has nothing to
    // do with that fact, so it is reported rather than raised.
    [Test]
    public async Task AnUnknownTurn_IsReportedAsNotFoundWithoutAnError()
    {
        _repository
            .FindMostRecentByUserAndHashAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SkillSelectionTrajectory?)null);

        var result = await _handler.Handle(Command(Message), CancellationToken.None);

        result.Found.ShouldBeFalse();
        result.TrajectoryId.ShouldBeNull();
        await _repository.DidNotReceive().UpdateAsync(
            Arg.Any<SkillSelectionTrajectory>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARepeatedThumbsUp_ChangesNothingAndStillReportsTheTurn()
    {
        var trajectory = GivenTrajectory();
        trajectory.Helpful = true;

        var result = await _handler.Handle(Command(Message), CancellationToken.None);

        result.Found.ShouldBeTrue();
        await _repository.DidNotReceive().UpdateAsync(
            Arg.Any<SkillSelectionTrajectory>(), Arg.Any<CancellationToken>());
    }

    // Praise must not erase a complaint. A user who first said "helpful" and then corrected the same
    // turn has said two things, and the fitness quote may see both.
    [Test]
    public async Task ACorrectedTurn_KeepsItsCorrectionWhenItIsAlsoMarkedHelpful()
    {
        var trajectory = GivenTrajectory();
        trajectory.WasCorrected = true;
        trajectory.CorrectionType = CorrectionTypes.WrongSkill;

        await _handler.Handle(Command(Message), CancellationToken.None);

        trajectory.Helpful.ShouldBe(true);
        trajectory.WasCorrected.ShouldBeTrue();
        trajectory.CorrectionType.ShouldBe(CorrectionTypes.WrongSkill);
    }

    [Test]
    public void AnEmptyUserId_IsRejected()
    {
        var command = new SubmitHelpfulFeedbackCommand { UserId = string.Empty, UserMessage = Message };

        Should.Throw<ArgumentException>(() => _handler.Handle(command, CancellationToken.None));
    }

    [Test]
    public void AnEmptyMessage_IsRejected()
    {
        var command = new SubmitHelpfulFeedbackCommand { UserId = UserId, UserMessage = "   " };

        Should.Throw<ArgumentException>(() => _handler.Handle(command, CancellationToken.None));
    }

    private static SubmitHelpfulFeedbackCommand Command(string message) =>
        new() { UserId = UserId, UserMessage = message };

    private SkillSelectionTrajectory GivenTrajectory()
    {
        var trajectory = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            UserMessageHash = MessageNormalizer.Hash(Message)
        };

        _repository
            .FindMostRecentByUserAndHashAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(trajectory);

        return trajectory;
    }
}
