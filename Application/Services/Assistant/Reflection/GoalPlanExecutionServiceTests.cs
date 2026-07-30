// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GoalPlanExecutionService — the Phase 4 unattended-execution gate of the
/// self-directed-goals roadmap. Covers every brake in isolation (feature flag off, candidate not
/// approved, missing PlanId, confidence Low/Unknown, an admin below Autonomous, no admin at all, an
/// admin without any stored autonomy level, and a missing/empty frozen permissions CSV) each proving
/// execution is never started, plus the happy path
/// proving the frozen owner permissions and the fixed audit user name reach the SkillExecutionContext
/// handed to IPlanChatService.StartBackgroundExecution.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

[TestFixture]
public class GoalPlanExecutionServiceTests
{
    private const string OwnerUserId = "11111111-1111-1111-1111-111111111111";
    private const string AdminAId = "22222222-2222-2222-2222-222222222222";
    private const string AdminBId = "33333333-3333-3333-3333-333333333333";

    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentAutonomyPreferenceRepository _autonomyRepository = null!;
    private IPlanChatService _planChatService = null!;
    private BackgroundServiceOptions _options = null!;
    private GoalPlanExecutionService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AdminAId });
        _autonomyRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _autonomyRepository.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);
        _planChatService = Substitute.For<IPlanChatService>();
        _options = new BackgroundServiceOptions { GoalReflectionExecution = true };
        _sut = new GoalPlanExecutionService(
            _goalCandidateRepository, _audienceResolver, _autonomyRepository, _planChatService,
            Options.Create(_options), NullLogger<GoalPlanExecutionService>.Instance);
    }

    private static GoalCandidate MakeCandidate(
        string status = GoalCandidateStatus.Approved,
        Guid? planId = null,
        string confidence = GoalCandidateConfidence.High,
        string? ownerPermissionsCsv = "CanEditClients,CanEditShifts",
        string? userId = OwnerUserId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Title = "Tighten contract renewals",
        Rationale = "seen 3x this week",
        Status = status,
        Confidence = confidence,
        SignalSource = "unstaffed_shift",
        DedupHash = "hash",
        OwnerPermissionsCsv = ownerPermissionsCsv,
        PlanId = planId ?? Guid.NewGuid()
    };

    [Test]
    public async Task ExecuteForCandidateAsync_FlagOff_DoesNotStartExecution()
    {
        _options.GoalReflectionExecution = false;

        var result = await _sut.ExecuteForCandidateAsync(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeFalse();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_CandidateNotApproved_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Proposed);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_PlanIdMissing_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Approved);
        candidate.PlanId = null;
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_UnknownCandidate_DoesNotStartExecution()
    {
        _goalCandidateRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((GoalCandidate?)null);

        var result = await _sut.ExecuteForCandidateAsync(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_ConfidenceLow_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(confidence: GoalCandidateConfidence.Low);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_ConfidenceUnknown_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(confidence: GoalCandidateConfidence.Unknown);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_OneAdminBelowAutonomous_DoesNotStartExecution()
    {
        var candidate = MakeCandidate();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AdminAId, AdminBId });
        _autonomyRepository.GetAsync(AdminAId, Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminAId, Level = AutonomyLevel.FullyAutonomous });
        _autonomyRepository.GetAsync(AdminBId, Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminBId, Level = AutonomyLevel.Assisted });

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_OneAdminOnPropose_DoesNotStartExecution()
    {
        var candidate = MakeCandidate();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _autonomyRepository.GetAsync(AdminAId, Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminAId, Level = AutonomyLevel.Propose });

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_NoAdminExists_DoesNotStartExecution()
    {
        var candidate = MakeCandidate();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_OwnerPermissionsCsvNull_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(ownerPermissionsCsv: null);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_OwnerPermissionsCsvEmpty_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(ownerPermissionsCsv: "   ");
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_OwnerUserIdNotAGuid_DoesNotStartExecution()
    {
        var candidate = MakeCandidate(userId: "not-a-guid");
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceiveWithAnyArgs().StartBackgroundExecution(default, default!, default);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_AllBrakesPass_StartsExecutionWithFrozenPermissionsAndAuditName()
    {
        var candidate = MakeCandidate();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _autonomyRepository.GetAsync(AdminAId, Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminAId, Level = AutonomyLevel.Autonomous });

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeTrue();
        _planChatService.Received(1).StartBackgroundExecution(
            candidate.PlanId!.Value,
            Arg.Is<SkillExecutionContext>(c =>
                c.UserId == Guid.Parse(OwnerUserId) &&
                c.UserName == "Klacksy self-reflection" &&
                c.BypassAutonomyGate &&
                !c.SupportsUiActions &&
                c.SessionId == $"self-reflection:{candidate.Id}" &&
                c.UserPermissions.Contains("CanEditClients") &&
                c.UserPermissions.Contains("CanEditShifts")),
            false);
    }

    [Test]
    public async Task ExecuteForCandidateAsync_AdminWithoutStoredAutonomyLevel_DoesNotStartExecution()
    {
        var candidate = MakeCandidate();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }

    [Test]
    public async Task ExecuteForCandidateAsync_SecondAdminWithoutStoredAutonomyLevel_DoesNotStartExecution()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AdminAId, AdminBId });
        var candidate = MakeCandidate();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _autonomyRepository.GetAsync(AdminAId, Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = AdminAId, Level = AutonomyLevel.FullyAutonomous });

        var result = await _sut.ExecuteForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeFalse();
        _planChatService.DidNotReceive().StartBackgroundExecution(
            Arg.Any<Guid>(), Arg.Any<SkillExecutionContext>(), Arg.Any<bool>());
    }
}
