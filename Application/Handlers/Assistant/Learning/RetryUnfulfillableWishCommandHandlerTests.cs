// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for handing a wish the loop gave up on back to the learning loop. The state machine always
/// allowed unfulfillable to return to ready, but nothing could perform the move until now. What is worth
/// pinning is that only an unfulfillable wish can take it, and that the reopened wish starts with a full
/// attempt budget - carrying the spent one forward would drop it straight back out on its first attempt.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Assistant.Learning;

using Klacks.Api.Application.Commands.Assistant.Learning;
using Klacks.Api.Application.Handlers.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class RetryUnfulfillableWishCommandHandlerTests
{
    private ISkillLearningClusterRepository _clusters = null!;
    private RetryUnfulfillableWishCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _clusters = Substitute.For<ISkillLearningClusterRepository>();
        _clusters.TryRetryUnfulfillableAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        _handler = new RetryUnfulfillableWishCommandHandler(
            _clusters, Substitute.For<ILogger<RetryUnfulfillableWishCommandHandler>>());
    }

    private SkillLearningCluster Given(string status)
    {
        var cluster = new SkillLearningCluster
        {
            Id = Guid.NewGuid(),
            Status = status,
            AttemptCount = SkillLearningDefaults.MaxLearningAttempts,
            LastError = "No existing skill can serve this wish."
        };

        _clusters.GetByIdAsync(cluster.Id, Arg.Any<CancellationToken>()).Returns(cluster);
        return cluster;
    }

    [Test]
    public async Task AnUnfulfillableWish_IsHandedBackToTheLoop()
    {
        var cluster = Given(SkillLearningClusterStatuses.Unfulfillable);

        var result = await _handler.Handle(
            new RetryUnfulfillableWishCommand(cluster.Id), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Error.ShouldBeNull();
        await _clusters.Received(1).TryRetryUnfulfillableAsync(cluster.Id, Arg.Any<CancellationToken>());
    }

    // The endpoint belongs to the unfulfillable card. A wish in any other status reaching it means the
    // card is out of date, which is a request to refuse rather than a state to overwrite.
    [TestCase(SkillLearningClusterStatuses.Ready)]
    [TestCase(SkillLearningClusterStatuses.Learning)]
    [TestCase(SkillLearningClusterStatuses.Dismissed)]
    [TestCase(SkillLearningClusterStatuses.LearnedPhrase)]
    public async Task AWishInAnyOtherStatus_IsRefused(string status)
    {
        var cluster = Given(status);

        var result = await _handler.Handle(
            new RetryUnfulfillableWishCommand(cluster.Id), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Error.ShouldNotBeNullOrWhiteSpace();
        await _clusters.DidNotReceive().TryRetryUnfulfillableAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnknownWish_ReportsNotFound()
    {
        _clusters.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((SkillLearningCluster?)null);

        (await _handler.Handle(new RetryUnfulfillableWishCommand(Guid.NewGuid()), CancellationToken.None))
            .Found.ShouldBeFalse();
    }

    // Another instance may have moved the wish on between the read and the write; the conditional update
    // is what decides, and losing it must not be reported as a reopened wish.
    [Test]
    public async Task AWishAnotherInstanceMovedOn_ReportsNotFound()
    {
        var cluster = Given(SkillLearningClusterStatuses.Unfulfillable);
        _clusters.TryRetryUnfulfillableAsync(cluster.Id, Arg.Any<CancellationToken>()).Returns(false);

        (await _handler.Handle(new RetryUnfulfillableWishCommand(cluster.Id), CancellationToken.None))
            .Found.ShouldBeFalse();
    }
}
