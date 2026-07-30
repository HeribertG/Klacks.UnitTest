// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GoalReflectionService — covers the catalogue-selection contract (the model returns
/// goal types, never prose; a type the user has no signal for is discarded; the same type twice in one
/// response is discarded; the persisted candidate carries the goal type, the interpolation parameters
/// and the catalogue's canonical English wording), the confidence contract (a missing, unparsable or
/// unexpected value always degrades to Unknown), the shadow/delivery flag contract (Status = Shadow
/// versus Status = Proposed, planners only) and the robustness contract (no signals means no LLM call,
/// broken JSON persists nothing, one user's failure never stops the others). IGoalSignalSource,
/// IGoalCandidateRepository, ICheapestModelResolver, ILLMProvider and IPlanningAudienceResolver are mocked.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using System.Text.Json;
using Klacks.Api.Application.Configuration;
using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

[TestFixture]
public class GoalReflectionServiceTests
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const int LookbackDays = 7;
    private const int OccurrenceCount = 3;

    private IGoalSignalSource _signalSource = null!;
    private IGoalCandidateRepository _goalCandidateRepository = null!;
    private ICheapestModelResolver _cheapestModelResolver = null!;
    private IPlanningAudienceResolver _planningAudienceResolver = null!;
    private ILLMProvider _provider = null!;
    private BackgroundServiceOptions _options = null!;
    private GoalReflectionService _sut = null!;
    private List<GoalCandidate> _persistedCandidates = null!;

    [SetUp]
    public void Setup()
    {
        _signalSource = Substitute.For<IGoalSignalSource>();
        _goalCandidateRepository = Substitute.For<IGoalCandidateRepository>();
        _cheapestModelResolver = Substitute.For<ICheapestModelResolver>();
        _planningAudienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _provider = Substitute.For<ILLMProvider>();
        _options = new BackgroundServiceOptions();

        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>());

        _persistedCandidates = new List<GoalCandidate>();
        _goalCandidateRepository
            .When(r => r.AddAsync(Arg.Any<GoalCandidate>(), Arg.Any<CancellationToken>()))
            .Do(ci => _persistedCandidates.Add(ci.Arg<GoalCandidate>()));

        var model = new LLMModel
        {
            Id = Guid.NewGuid(),
            ModelId = "cheap-model",
            ModelName = "Cheap Model",
            ApiModelId = "cheap-model-api-id",
            ProviderId = "test-provider",
            IsEnabled = true
        };
        _cheapestModelResolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns((model, _provider));

        _sut = new GoalReflectionService(
            _signalSource,
            _goalCandidateRepository,
            _cheapestModelResolver,
            _planningAudienceResolver,
            Options.Create(_options),
            NullLogger<GoalReflectionService>.Instance);
    }

    private static GoalSignal Signal(
        string userId,
        string kind = AgentTriggerKinds.UnstaffedShift,
        string summary = "recurring observation") =>
        new(userId, kind, summary, OccurrenceCount, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow, LookbackDays);

    private static string Response(params string[] candidateJson) =>
        "{\"candidates\":[" + string.Join(",", candidateJson) + "]}";

    private static string Candidate(string goalType, string? confidence = "high") =>
        confidence == null
            ? $"{{\"goalType\":\"{goalType}\"}}"
            : $"{{\"goalType\":\"{goalType}\",\"confidence\":\"{confidence}\"}}";

    private void SetLlmResponse(string content) =>
        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LLMProviderResponse { Success = true, Content = content });

    [Test]
    public async Task RunReflectionCycleAsync_NoSignals_ReturnsZeroWithoutLlmCallOrPersistence()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal>());

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(0);
        await _cheapestModelResolver.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
        _persistedCandidates.ShouldBeEmpty();
    }

    [Test]
    public async Task RunReflectionCycleAsync_TwoSelectedGoalTypes_PersistsBothAsShadow()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal>
        {
            Signal(UserA, AgentTriggerKinds.UnstaffedShift),
            Signal(UserA, AgentTriggerKinds.ContractExpiringSoon)
        });
        SetLlmResponse(Response(
            Candidate(AgentTriggerKinds.UnstaffedShift),
            Candidate(AgentTriggerKinds.ContractExpiringSoon, "low")));

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(2);
        _persistedCandidates.Count.ShouldBe(2);
        _persistedCandidates.ShouldAllBe(c => c.Status == GoalCandidateStatus.Shadow);
        _persistedCandidates.ShouldAllBe(c => c.OwnerPermissionsCsv == null);
        _persistedCandidates.ShouldAllBe(c => c.UserId == UserA);
        _persistedCandidates.Single(c => c.GoalType == AgentTriggerKinds.UnstaffedShift)
            .Confidence.ShouldBe(GoalCandidateConfidence.High);
        _persistedCandidates.Single(c => c.GoalType == AgentTriggerKinds.ContractExpiringSoon)
            .Confidence.ShouldBe(GoalCandidateConfidence.Low);
    }

    [Test]
    public async Task RunReflectionCycleAsync_SelectedGoalType_PersistsCatalogueWordingAndParameters()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GoalSignal> { Signal(UserA, AgentTriggerKinds.TargetHoursDrift) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.TargetHoursDrift)));

        await _sut.RunReflectionCycleAsync();

        var definition = GoalTypeCatalog.ByTriggerKind[AgentTriggerKinds.TargetHoursDrift];
        var persisted = _persistedCandidates.ShouldHaveSingleItem();
        persisted.GoalType.ShouldBe(AgentTriggerKinds.TargetHoursDrift);
        persisted.SignalSource.ShouldBe(AgentTriggerKinds.TargetHoursDrift);
        persisted.Title.ShouldBe(definition.PlannerTitle);
        persisted.Rationale.ShouldBe(string.Format(definition.PlannerRationaleFormat, OccurrenceCount, LookbackDays));

        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(persisted.RationaleParamsJson!)!;
        parameters[GoalCandidateRationaleParams.Count].ShouldBe(OccurrenceCount.ToString());
        parameters[GoalCandidateRationaleParams.Days].ShouldBe(LookbackDays.ToString());
    }

    [Test]
    public async Task RunReflectionCycleAsync_PromptCarriesNoTriggerIdentifierInsideTheDescription()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GoalSignal> { Signal(UserA, AgentTriggerKinds.TargetHoursDrift, "an hour deviation was noticed") });
        SetLlmResponse(Response());

        await _sut.RunReflectionCycleAsync();

        await _provider.Received(1).ProcessAsync(
            Arg.Is<LLMProviderRequest>(r => r.Message.Contains("an hour deviation was noticed")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunReflectionCycleAsync_GoalTypeWithoutMatchingSignal_IsDiscarded()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GoalSignal> { Signal(UserA, AgentTriggerKinds.UnstaffedShift) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.OrderImportFailed)));

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(0);
        _persistedCandidates.ShouldBeEmpty();
    }

    [Test]
    public async Task RunReflectionCycleAsync_UnknownGoalType_IsDiscarded()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(Response(Candidate("invent_something_new")));

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(0);
        _persistedCandidates.ShouldBeEmpty();
    }

    [Test]
    public async Task RunReflectionCycleAsync_SameGoalTypeTwiceInOneResponse_PersistsItOnce()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GoalSignal> { Signal(UserA, AgentTriggerKinds.PeriodOverdue) });
        SetLlmResponse(Response(
            Candidate(AgentTriggerKinds.PeriodOverdue),
            Candidate(AgentTriggerKinds.PeriodOverdue, "low")));

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(1);
        _persistedCandidates.ShouldHaveSingleItem().GoalType.ShouldBe(AgentTriggerKinds.PeriodOverdue);
    }

    [Test]
    public async Task RunReflectionCycleAsync_ConfidenceFieldMissing_DefaultsToUnknown()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.UnstaffedShift, confidence: null)));

        await _sut.RunReflectionCycleAsync();

        _persistedCandidates.ShouldHaveSingleItem().Confidence.ShouldBe(GoalCandidateConfidence.Unknown);
    }

    [Test]
    public async Task RunReflectionCycleAsync_ConfidenceFieldUnparsableValue_DefaultsToUnknown()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.UnstaffedShift, "maybe")));

        await _sut.RunReflectionCycleAsync();

        _persistedCandidates.ShouldHaveSingleItem().Confidence.ShouldBe(GoalCandidateConfidence.Unknown);
    }

    [Test]
    public async Task RunReflectionCycleAsync_BrokenJson_DoesNotCrashAndPersistsNothing()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse("this is not json at all");

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(0);
        _persistedCandidates.ShouldBeEmpty();
    }

    [Test]
    public async Task RunReflectionCycleAsync_DedupHit_SkipsPersistence()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.UnstaffedShift)));
        _goalCandidateRepository
            .ExistsRecentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(0);
        _persistedCandidates.ShouldBeEmpty();
    }

    [Test]
    public async Task RunReflectionCycleAsync_OneUserThrows_OtherUserIsStillProcessed()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GoalSignal>
            {
                Signal(UserA, AgentTriggerKinds.UnstaffedShift, "throws-marker"),
                Signal(UserB, AgentTriggerKinds.UnstaffedShift, "healthy-marker")
            });

        _provider.ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<LLMProviderRequest>();
                if (request.Message.Contains("throws-marker"))
                {
                    throw new InvalidOperationException("simulated provider failure");
                }

                return new LLMProviderResponse
                {
                    Success = true,
                    Content = Response(Candidate(AgentTriggerKinds.UnstaffedShift))
                };
            });

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(1);
        _persistedCandidates.ShouldHaveSingleItem().UserId.ShouldBe(UserB);
    }

    [Test]
    public async Task RunReflectionCycleAsync_DeliveryFlagOff_PersistsAsShadow()
    {
        _options.GoalReflectionDelivery = false;
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.UnstaffedShift)));

        await _sut.RunReflectionCycleAsync();

        _persistedCandidates.ShouldHaveSingleItem().Status.ShouldBe(GoalCandidateStatus.Shadow);
        await _planningAudienceResolver.DidNotReceive().GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunReflectionCycleAsync_DeliveryFlagOn_PersistsAsProposedWithoutFreezingPermissions()
    {
        _options.GoalReflectionDelivery = true;
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase) { UserA });
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(Response(Candidate(AgentTriggerKinds.UnstaffedShift)));

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(1);
        var persisted = _persistedCandidates.ShouldHaveSingleItem();
        persisted.Status.ShouldBe(GoalCandidateStatus.Proposed);
        persisted.OwnerPermissionsCsv.ShouldBeNull();
    }

    [Test]
    public async Task RunReflectionCycleAsync_DeliveryFlagOnAndUserIsNotPlanner_ProducesNoCandidateAndSkipsLlmCall()
    {
        _options.GoalReflectionDelivery = true;
        _planningAudienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<string>)new HashSet<string>());
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(0);
        _persistedCandidates.ShouldBeEmpty();
        await _cheapestModelResolver.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().ProcessAsync(Arg.Any<LLMProviderRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Constructor_TakesNoDeliveryDependency_Phase1IsShadowOnly()
    {
        var forbiddenNameFragments = new[] { "Notification", "Hub", "Inbox" };
        var parameters = typeof(GoalReflectionService).GetConstructors().Single().GetParameters();

        foreach (var parameter in parameters)
        {
            var typeName = parameter.ParameterType.Name;
            forbiddenNameFragments.Any(typeName.Contains).ShouldBeFalse(
                $"GoalReflectionService must not depend on '{typeName}' — Phase 1 is shadow-mode only, no delivery.");
        }
    }
}
