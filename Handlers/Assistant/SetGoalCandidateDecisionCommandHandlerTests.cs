// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SetGoalCandidateDecisionCommandHandler — the Phase 2 read-only decision gate.
/// Covers: approval sets Status=Approved and freezes OwnerPermissionsCsv from the command's
/// UserPermissions; rejection sets Status=Rejected and leaves OwnerPermissionsCsv null; a candidate
/// owned by a different user is refused and nothing is persisted; a candidate already in a terminal
/// status is refused and not re-decided; an invalid decision value is refused before the repository
/// is even queried; and a structural guard that the handler's constructor cannot take any dependency
/// capable of drafting a plan or executing a skill, because Phase 2 must never do either.
/// </summary>

using System.Linq;
using System.Reflection;
using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class SetGoalCandidateDecisionCommandHandlerTests
{
    private const string OwnerUserId = "user-a";
    private const string OtherUserId = "user-b";

    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private SetGoalCandidateDecisionCommandHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _sut = new SetGoalCandidateDecisionCommandHandler(_goalCandidateRepository);
    }

    private static GoalCandidate MakeCandidate(string userId, string status = "shadow") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Title = "Tighten contract renewals",
        Rationale = "seen 3x this week",
        Confidence = GoalCandidateConfidence.High,
        SignalSource = "unstaffed_shift",
        Status = status,
        DedupHash = "hash"
    };

    [Test]
    public async Task Handle_Approved_SetsStatusAndFreezesOwnerPermissions()
    {
        var candidate = MakeCandidate(OwnerUserId, GoalCandidateStatus.Proposed);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        var before = DateTime.UtcNow;

        var result = await _sut.Handle(new SetGoalCandidateDecisionCommand
        {
            Id = candidate.Id,
            UserId = OwnerUserId,
            Decision = GoalCandidateStatus.Approved,
            UserPermissions = new[] { "clients.read", "clients.write" }
        }, CancellationToken.None);

        result.ShouldBeTrue();
        candidate.Status.ShouldBe(GoalCandidateStatus.Approved);
        candidate.OwnerPermissionsCsv.ShouldBe("clients.read,clients.write");
        candidate.DecidedUtc.ShouldNotBeNull();
        candidate.DecidedUtc!.Value.ShouldBeGreaterThanOrEqualTo(before);
        await _goalCandidateRepository.Received(1).UpdateAsync(candidate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_Rejected_SetsStatusAndLeavesOwnerPermissionsNull()
    {
        var candidate = MakeCandidate(OwnerUserId, GoalCandidateStatus.Proposed);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.Handle(new SetGoalCandidateDecisionCommand
        {
            Id = candidate.Id,
            UserId = OwnerUserId,
            Decision = GoalCandidateStatus.Rejected,
            UserPermissions = new[] { "clients.read" }
        }, CancellationToken.None);

        result.ShouldBeTrue();
        candidate.Status.ShouldBe(GoalCandidateStatus.Rejected);
        candidate.OwnerPermissionsCsv.ShouldBeNull();
        candidate.DecidedUtc.ShouldNotBeNull();
        await _goalCandidateRepository.Received(1).UpdateAsync(candidate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_CandidateOwnedByDifferentUser_ReturnsFalseAndDoesNotUpdate()
    {
        var candidate = MakeCandidate(OtherUserId, GoalCandidateStatus.Proposed);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.Handle(new SetGoalCandidateDecisionCommand
        {
            Id = candidate.Id,
            UserId = OwnerUserId,
            Decision = GoalCandidateStatus.Approved,
            UserPermissions = new[] { "clients.read" }
        }, CancellationToken.None);

        result.ShouldBeFalse();
        candidate.Status.ShouldBe(GoalCandidateStatus.Proposed);
        candidate.OwnerPermissionsCsv.ShouldBeNull();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [TestCase(GoalCandidateStatus.Approved)]
    [TestCase(GoalCandidateStatus.Rejected)]
    [TestCase(GoalCandidateStatus.Expired)]
    public async Task Handle_CandidateAlreadyTerminal_ReturnsFalseAndDoesNotReDecide(string terminalStatus)
    {
        var candidate = MakeCandidate(OwnerUserId, terminalStatus);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.Handle(new SetGoalCandidateDecisionCommand
        {
            Id = candidate.Id,
            UserId = OwnerUserId,
            Decision = GoalCandidateStatus.Approved,
            UserPermissions = new[] { "clients.read" }
        }, CancellationToken.None);

        result.ShouldBeFalse();
        candidate.Status.ShouldBe(terminalStatus);
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [TestCase("shadow")]
    [TestCase("")]
    [TestCase("approve")]
    public async Task Handle_InvalidDecisionValue_ReturnsFalseWithoutQueryingRepository(string invalidDecision)
    {
        var result = await _sut.Handle(new SetGoalCandidateDecisionCommand
        {
            Id = Guid.NewGuid(),
            UserId = OwnerUserId,
            Decision = invalidDecision,
            UserPermissions = new[] { "clients.read" }
        }, CancellationToken.None);

        result.ShouldBeFalse();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task Handle_UnknownId_ReturnsFalseAndDoesNotUpdate()
    {
        _goalCandidateRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((GoalCandidate?)null);

        var result = await _sut.Handle(new SetGoalCandidateDecisionCommand
        {
            Id = Guid.NewGuid(),
            UserId = OwnerUserId,
            Decision = GoalCandidateStatus.Approved,
            UserPermissions = new[] { "clients.read" }
        }, CancellationToken.None);

        result.ShouldBeFalse();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public void Constructor_TakesNoPlanningOrExecutionDependency_Phase2IsDecisionOnly()
    {
        var forbiddenNameFragments = new[] { "Planning", "PlanChat", "SkillExecutor", "PlanStep" };
        var parameters = typeof(SetGoalCandidateDecisionCommandHandler).GetConstructors().Single().GetParameters();

        foreach (var parameter in parameters)
        {
            var typeName = parameter.ParameterType.Name;
            forbiddenNameFragments.Any(typeName.Contains).ShouldBeFalse(
                $"SetGoalCandidateDecisionCommandHandler must not depend on '{typeName}' — Phase 2 only records a decision, it never plans or executes.");
        }
    }
}
