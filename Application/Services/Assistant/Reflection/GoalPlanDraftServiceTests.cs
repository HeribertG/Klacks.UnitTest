// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GoalPlanDraftService — the Phase 3 plan-draft step of the self-directed-goals
/// roadmap. Covers: a candidate that is not (yet) Approved never gets a plan; a candidate that
/// already has a PlanId is never drafted a second time; a successful draft creates the plan with
/// Origin = SelfReflection and links GoalCandidate.PlanId; a failure while drafting is swallowed and
/// returns null without mutating the candidate; a structural guard that the constructor cannot take
/// any dependency capable of executing a plan step, because Phase 3 only drafts and shows, never runs.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using System.Linq;
using System.Reflection;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Planning;
using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute.ExceptionExtensions;

[TestFixture]
public class GoalPlanDraftServiceTests
{
    private const string OwnerUserId = "user-a";

    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private IPlanChatService _planChatService = null!;
    private BackgroundServiceOptions _options = null!;
    private GoalPlanDraftService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _planChatService = Substitute.For<IPlanChatService>();
        _options = new BackgroundServiceOptions { GoalReflectionPlanDrafting = true };
        _sut = new GoalPlanDraftService(
            _goalCandidateRepository, _planChatService, Options.Create(_options), NullLogger<GoalPlanDraftService>.Instance);
    }

    private static GoalCandidate MakeCandidate(string status = "approved", Guid? planId = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = OwnerUserId,
        Title = "Tighten contract renewals",
        Rationale = "seen 3x this week",
        Confidence = GoalCandidateConfidence.High,
        SignalSource = "unstaffed_shift",
        Status = status,
        DedupHash = "hash",
        PlanId = planId
    };

    private static AgentPlan MakePlan(string stepsJson = "[{\"order\":1}]") => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        Goal = "Tighten contract renewals: seen 3x this week",
        StepsJson = stepsJson,
        Status = "drafting"
    };

    [Test]
    public async Task DraftForCandidateAsync_CandidateNotApproved_ReturnsNullWithoutDrafting()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Proposed);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.DraftForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeNull();
        await _planChatService.DidNotReceiveWithAnyArgs().CreatePlanAsync(default!, default!, default, default, default!);
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task DraftForCandidateAsync_PlanIdAlreadySet_ReturnsNullWithoutDraftingAgain()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Approved, planId: Guid.NewGuid());
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);

        var result = await _sut.DraftForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeNull();
        await _planChatService.DidNotReceiveWithAnyArgs().CreatePlanAsync(default!, default!, default, default, default!);
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task DraftForCandidateAsync_UnknownCandidate_ReturnsNull()
    {
        _goalCandidateRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((GoalCandidate?)null);

        var result = await _sut.DraftForCandidateAsync(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeNull();
        await _planChatService.DidNotReceiveWithAnyArgs().CreatePlanAsync(default!, default!, default, default, default!);
    }

    [Test]
    public async Task DraftForCandidateAsync_Success_CreatesSelfReflectionPlanAndLinksCandidate()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Approved);
        var plan = MakePlan();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _planChatService.CreatePlanAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(plan);

        var result = await _sut.DraftForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBe(plan.Id);
        candidate.PlanId.ShouldBe(plan.Id);
        await _planChatService.Received(1).CreatePlanAsync(
            Arg.Any<string>(), OwnerUserId, null, Arg.Any<CancellationToken>(), AgentPlanOrigin.SelfReflection);
        await _goalCandidateRepository.Received(1).UpdateAsync(candidate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DraftForCandidateAsync_GoalTextIncludesTitleAndRationale()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Approved);
        var plan = MakePlan();
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _planChatService.CreatePlanAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(plan);

        await _sut.DraftForCandidateAsync(candidate.Id, CancellationToken.None);

        await _planChatService.Received(1).CreatePlanAsync(
            Arg.Is<string>(g => g.Contains(candidate.Title) && g.Contains(candidate.Rationale)),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>());
    }

    [Test]
    public async Task DraftForCandidateAsync_PlanningThrows_ReturnsNullAndLeavesCandidateUnchanged()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Approved);
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _planChatService.CreatePlanAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Throws(new InvalidOperationException("simulated planning failure"));

        var result = await _sut.DraftForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeNull();
        candidate.PlanId.ShouldBeNull();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task DraftForCandidateAsync_RepositoryLookupThrows_ReturnsNullWithoutCrashing()
    {
        _goalCandidateRepository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated repository failure"));

        var result = await _sut.DraftForCandidateAsync(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task DraftForCandidateAsync_FlagOff_ReturnsNullWithoutLookingUpCandidate()
    {
        _options.GoalReflectionPlanDrafting = false;

        var result = await _sut.DraftForCandidateAsync(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBeNull();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        await _planChatService.DidNotReceiveWithAnyArgs().CreatePlanAsync(default!, default!, default, default, default!);
    }

    [Test]
    public async Task DraftForCandidateAsync_PlanHasZeroSteps_ReturnsNullAndLeavesPlanIdUnsetForRetry()
    {
        var candidate = MakeCandidate(status: GoalCandidateStatus.Approved);
        var plan = MakePlan(stepsJson: "[]");
        _goalCandidateRepository.GetByIdAsync(candidate.Id, Arg.Any<CancellationToken>()).Returns(candidate);
        _planChatService.CreatePlanAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(plan);

        var result = await _sut.DraftForCandidateAsync(candidate.Id, CancellationToken.None);

        result.ShouldBeNull();
        candidate.PlanId.ShouldBeNull();
        await _goalCandidateRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public void Constructor_TakesNoExecutionDependency_Phase3IsDraftOnly()
    {
        var forbiddenNameFragments = new[] { "Executor" };
        var parameters = typeof(GoalPlanDraftService).GetConstructors().Single().GetParameters();

        foreach (var parameter in parameters)
        {
            var typeName = parameter.ParameterType.Name;
            forbiddenNameFragments.Any(typeName.Contains).ShouldBeFalse(
                $"GoalPlanDraftService must not depend on '{typeName}' — Phase 3 only drafts and shows a plan, it never executes one.");
        }
    }
}
