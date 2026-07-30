// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetGoalCandidatesQueryHandler — verifies that the requesting user's id and status
/// filter are forwarded unchanged to the repository (which owns the "only my own candidates" and
/// terminal-status filtering), that take is normalized to the configured default/maximum the same
/// way GetProactiveMessagesQueryHandler does, and that entities map to GoalCandidateDto correctly,
/// including the DecidedUtc field and the deliberate absence of OwnerPermissionsCsv.
/// </summary>

using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Handlers.Assistant;

[TestFixture]
public class GetGoalCandidatesQueryHandlerTests
{
    private const string OwnerUserId = "user-a";
    private const string OtherUserId = "user-b";

    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private GetGoalCandidatesQueryHandler _sut = null!;

    [SetUp]
    public void Setup()
    {
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _sut = new GetGoalCandidatesQueryHandler(_goalCandidateRepository);
    }

    private static GoalCandidate MakeCandidate(string userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Title = "Tighten contract renewals",
        Rationale = "seen 3x this week",
        Confidence = GoalCandidateConfidence.High,
        SignalSource = "unstaffed_shift",
        Status = GoalCandidateStatus.Shadow,
        DedupHash = "hash",
        CreateTime = new DateTime(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public async Task Handle_ForwardsUserIdStatusAndTakeToRepository()
    {
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate>());

        await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId, Status = GoalCandidateStatus.Proposed, Take = 5 }, CancellationToken.None);

        await _goalCandidateRepository.Received(1).GetForUserAsync(OwnerUserId, GoalCandidateStatus.Proposed, 5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NoTake_UsesDefaultListTake()
    {
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate>());

        await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId, Take = null }, CancellationToken.None);

        await _goalCandidateRepository.Received(1).GetForUserAsync(
            OwnerUserId, Arg.Any<string?>(), GoalCandidateInboxDefaults.DefaultListTake, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_NonPositiveTake_UsesDefaultListTake()
    {
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate>());

        await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId, Take = 0 }, CancellationToken.None);

        await _goalCandidateRepository.Received(1).GetForUserAsync(
            OwnerUserId, Arg.Any<string?>(), GoalCandidateInboxDefaults.DefaultListTake, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_TakeAboveMaximum_ClampsToMaxListTake()
    {
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate>());

        await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId, Take = GoalCandidateInboxDefaults.MaxListTake + 1 }, CancellationToken.None);

        await _goalCandidateRepository.Received(1).GetForUserAsync(
            OwnerUserId, Arg.Any<string?>(), GoalCandidateInboxDefaults.MaxListTake, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_OnlyReturnsCandidatesTheRepositoryScopedToTheRequestingUser()
    {
        var ownCandidate = MakeCandidate(OwnerUserId);
        var otherUsersCandidate = MakeCandidate(OtherUserId);
        _goalCandidateRepository
            .GetForUserAsync(OwnerUserId, Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { ownCandidate });
        _goalCandidateRepository
            .GetForUserAsync(OtherUserId, Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { otherUsersCandidate });

        var result = await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId }, CancellationToken.None);

        result.Count.ShouldBe(1);
        result.Single().Id.ShouldBe(ownCandidate.Id);
        result.ShouldNotContain(dto => dto.Id == otherUsersCandidate.Id);
    }

    [Test]
    public async Task Handle_MapsCandidateToDtoWithoutOwnerPermissionsCsv()
    {
        var candidate = MakeCandidate(OwnerUserId);
        candidate.OwnerPermissionsCsv = "clients.read,clients.write";
        candidate.DecidedUtc = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });

        var result = await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId }, CancellationToken.None);

        var dto = result.Single();
        dto.Id.ShouldBe(candidate.Id);
        dto.Title.ShouldBe(candidate.Title);
        dto.Rationale.ShouldBe(candidate.Rationale);
        dto.Confidence.ShouldBe(candidate.Confidence);
        dto.SignalSource.ShouldBe(candidate.SignalSource);
        dto.Status.ShouldBe(candidate.Status);
        dto.CreatedUtc.ShouldBe(candidate.CreateTime);
        dto.DecidedUtc.ShouldBe(candidate.DecidedUtc);
    }

    [Test]
    public async Task Handle_CandidateWithGoalType_ResolvesCatalogueKeysAndParameters()
    {
        var candidate = MakeCandidate(OwnerUserId);
        candidate.GoalType = AgentTriggerKinds.TargetHoursDrift;
        candidate.RationaleParamsJson = "{\"count\":\"7\",\"days\":\"7\"}";
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });

        var result = await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId }, CancellationToken.None);

        var definition = GoalTypeCatalog.ByTriggerKind[AgentTriggerKinds.TargetHoursDrift];
        var dto = result.Single();
        dto.GoalType.ShouldBe(AgentTriggerKinds.TargetHoursDrift);
        dto.TitleKey.ShouldBe(definition.TitleKey);
        dto.RationaleKey.ShouldBe(definition.RationaleKey);
        dto.RationaleParams!["count"].ShouldBe("7");
        dto.RationaleParams!["days"].ShouldBe("7");
    }

    [Test]
    public async Task Handle_CandidateWithoutGoalType_LeavesKeysNullSoTheClientFallsBackToStoredText()
    {
        var candidate = MakeCandidate(OwnerUserId);
        candidate.GoalType = null;
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });

        var result = await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId }, CancellationToken.None);

        var dto = result.Single();
        dto.TitleKey.ShouldBeNull();
        dto.RationaleKey.ShouldBeNull();
        dto.RationaleParams.ShouldBeNull();
        dto.Title.ShouldBe(candidate.Title);
    }

    [Test]
    public async Task Handle_UnparsableRationaleParams_StillListsTheCandidate()
    {
        var candidate = MakeCandidate(OwnerUserId);
        candidate.GoalType = AgentTriggerKinds.TargetHoursDrift;
        candidate.RationaleParamsJson = "{not json";
        _goalCandidateRepository
            .GetForUserAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoalCandidate> { candidate });

        var result = await _sut.Handle(new GetGoalCandidatesQuery { UserId = OwnerUserId }, CancellationToken.None);

        var dto = result.Single();
        dto.RationaleParams.ShouldBeNull();
        dto.TitleKey.ShouldNotBeNull();
    }
}
