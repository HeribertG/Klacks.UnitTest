// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LockConflictDetector — covers lock-indicator detection,
/// lock-level extraction, work-id extraction, and non-lock failure skipping.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class LockConflictDetectorTests
{
    private IAgentSkillExecutionRepository _repo = null!;
    private LockConflictDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IAgentSkillExecutionRepository>();
        _sut = new LockConflictDetector(_repo, NullLogger<LockConflictDetector>.Instance);
    }

    private static AgentSkillExecution Failed(string errorMessage) => new()
    {
        Id = Guid.NewGuid(),
        ToolName = "place_work",
        Success = false,
        ErrorMessage = errorMessage,
        CreateTime = DateTime.UtcNow.AddMinutes(-30)
    };

    [Test]
    public async Task DetectAsync_NoFailures_ReturnsEmpty()
    {
        _repo.GetFailedSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkillExecution>());

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_GenericFailure_NoLockKeyword_Skips()
    {
        _repo.GetFailedSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkillExecution>
            {
                Failed("Invalid groupId 'foo'.")
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_LockKeyword_EmitsEvent_WithLockLevelExtracted()
    {
        var workId = Guid.NewGuid();
        _repo.GetFailedSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkillExecution>
            {
                Failed($"Work workId={workId} is locked at level 2 and cannot be modified.")
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var locked = events.Single() as LockConflictDetectedTriggerEvent;
        Assert.That(locked, Is.Not.Null);
        Assert.That(locked!.LockLevel, Is.EqualTo(2));
        Assert.That(locked.WorkId, Is.EqualTo(workId));
    }

    [Test]
    public async Task DetectAsync_LockKeyword_DefaultsToLevel1_WhenNoDigit()
    {
        _repo.GetFailedSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSkillExecution>
            {
                Failed("Work is locked, cannot continue.")
            });

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var locked = events.Single() as LockConflictDetectedTriggerEvent;
        Assert.That(locked!.LockLevel, Is.EqualTo(1));
    }
}
