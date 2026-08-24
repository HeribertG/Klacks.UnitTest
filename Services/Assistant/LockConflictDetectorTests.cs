// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LockConflictDetector — covers lock-indicator detection,
/// lock-level extraction, work-id extraction, and non-lock failure skipping.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class LockConflictDetectorTests
{
    private IAgentSkillExecutionRepository _repo = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private LockConflictDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repo = Substitute.For<IAgentSkillExecutionRepository>();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _sut = new LockConflictDetector(_repo, _groupScopeReader, NullLogger<LockConflictDetector>.Instance);
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

    [Test]
    public async Task DetectAsync_WorkInOneGroup_CarriesThatGroup_AndPreselectsItInTheActionParams()
    {
        var workId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        StubFailures($"Work workId={workId} is locked at level 2 and cannot be modified.");
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (workId, new[] { groupId }));

        var locked = (LockConflictDetectedTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(locked.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(locked.ActionParams![ProactiveActionParamKeys.GroupId], Is.EqualTo(groupId.ToString()));
        Assert.That(locked.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_WorkWhoseShiftIsInTwoGroups_CarriesBoth_NotOnlyTheFirst()
    {
        var workId = Guid.NewGuid();
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        StubFailures($"Work workId={workId} is locked at level 2 and cannot be modified.");
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (workId, new[] { firstGroupId, secondGroupId }));

        var locked = (LockConflictDetectedTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(locked.GroupIds, Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task DetectAsync_UnparsableWorkId_CarriesNoGroup_AndIsNeverLookedUp()
    {
        // Without a work id there is nothing to resolve a group from, so the event stays unscoped and
        // RequiresGroupScope routes it to Admins rather than to every planner.
        StubFailures("Work is locked, cannot continue.");

        var locked = (LockConflictDetectedTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(locked.WorkId, Is.EqualTo(Guid.Empty));
        Assert.That(locked.GroupIds, Is.Empty);
        Assert.That(locked.ActionParams!.ContainsKey(ProactiveActionParamKeys.GroupId), Is.False);
        Assert.That(locked.RequiresGroupScope, Is.True);
        await _groupScopeReader.Received(1).GetGroupIdsByWorkIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_ManyConflicts_ResolvesGroupsInOneBatchedLookup_NeverOnePerConflict()
    {
        var messages = Enumerable.Range(0, 10)
            .Select(_ => $"Work workId={Guid.NewGuid()} is locked at level 2 and cannot be modified.")
            .ToArray();
        StubFailures(messages);

        await _sut.DetectAsync();

        await _groupScopeReader.Received(1).GetGroupIdsByWorkIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    private void StubFailures(params string[] errorMessages) =>
        _repo.GetFailedSinceAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(errorMessages.Select(Failed).ToList());
}
