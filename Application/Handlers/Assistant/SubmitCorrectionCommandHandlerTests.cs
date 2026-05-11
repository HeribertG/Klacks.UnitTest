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
    private ILogger<SubmitCorrectionCommandHandler> _logger = null!;
    private SubmitCorrectionCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _logger = Substitute.For<ILogger<SubmitCorrectionCommandHandler>>();
        _handler = new SubmitCorrectionCommandHandler(_repository, _logger);
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
