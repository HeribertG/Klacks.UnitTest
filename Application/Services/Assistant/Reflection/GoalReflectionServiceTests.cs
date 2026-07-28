// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GoalReflectionService — covers both the Phase 1 shadow-mode contract (no signals
/// means no LLM call and no persistence, a valid LLM response persists candidates with
/// Status = Shadow, a missing/unparsable/broken 'confidence' field always degrades to Unknown, a
/// dedup hit is skipped, one user's failure never stops reflection for the other users) and the
/// Phase 2 delivery-flag contract (delivery on persists Status = Proposed; delivery on filters out
/// non-planner users before the LLM is even called). IGoalSignalSource, IGoalCandidateRepository,
/// ICheapestModelResolver, ILLMProvider and IPlanningAudienceResolver are mocked.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using System.Reflection;
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

    private static GoalSignal Signal(string userId, string summary = "recurring signal") =>
        new(userId, AgentTriggerKinds.UnstaffedShift, summary, 3, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow);

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
    public async Task RunReflectionCycleAsync_ValidResponseWithTwoCandidates_PersistsBothAsShadow()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse(
            "{\"candidates\":[" +
            "{\"title\":\"Tighten contract renewals\",\"rationale\":\"seen 3x\",\"confidence\":\"high\"}," +
            "{\"title\":\"Review availability gaps\",\"rationale\":\"seen 2x\",\"confidence\":\"low\"}" +
            "]}");

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(2);
        _persistedCandidates.Count.ShouldBe(2);
        _persistedCandidates.ShouldAllBe(c => c.Status == GoalCandidateStatus.Shadow);
        _persistedCandidates.ShouldAllBe(c => c.OwnerPermissionsCsv == null);
        _persistedCandidates.ShouldAllBe(c => c.UserId == UserA);
        _persistedCandidates.Single(c => c.Title == "Tighten contract renewals").Confidence.ShouldBe(GoalCandidateConfidence.High);
        _persistedCandidates.Single(c => c.Title == "Review availability gaps").Confidence.ShouldBe(GoalCandidateConfidence.Low);
    }

    [Test]
    public async Task RunReflectionCycleAsync_ConfidenceFieldMissing_DefaultsToUnknown()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse("{\"candidates\":[{\"title\":\"Do the thing\",\"rationale\":\"why not\"}]}");

        await _sut.RunReflectionCycleAsync();

        _persistedCandidates.Count.ShouldBe(1);
        _persistedCandidates.Single().Confidence.ShouldBe(GoalCandidateConfidence.Unknown);
    }

    [Test]
    public async Task RunReflectionCycleAsync_ConfidenceFieldUnparsableValue_DefaultsToUnknown()
    {
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse("{\"candidates\":[{\"title\":\"Do the thing\",\"rationale\":\"why not\",\"confidence\":\"maybe\"}]}");

        await _sut.RunReflectionCycleAsync();

        _persistedCandidates.Count.ShouldBe(1);
        _persistedCandidates.Single().Confidence.ShouldBe(GoalCandidateConfidence.Unknown);
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
        SetLlmResponse("{\"candidates\":[{\"title\":\"Already proposed\",\"rationale\":\"again\",\"confidence\":\"high\"}]}");
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
            .Returns(new List<GoalSignal> { Signal(UserA, "throws-marker"), Signal(UserB, "healthy-marker") });

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
                    Content = "{\"candidates\":[{\"title\":\"Healthy candidate\",\"rationale\":\"ok\",\"confidence\":\"high\"}]}"
                };
            });

        var result = await _sut.RunReflectionCycleAsync();

        result.ShouldBe(1);
        _persistedCandidates.Count.ShouldBe(1);
        _persistedCandidates.Single().UserId.ShouldBe(UserB);
        _persistedCandidates.Single().Title.ShouldBe("Healthy candidate");
    }

    [Test]
    public async Task RunReflectionCycleAsync_DeliveryFlagOff_PersistsAsShadow()
    {
        _options.GoalReflectionDelivery = false;
        _signalSource.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<GoalSignal> { Signal(UserA) });
        SetLlmResponse("{\"candidates\":[{\"title\":\"Tighten contract renewals\",\"rationale\":\"seen 3x\",\"confidence\":\"high\"}]}");

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
        SetLlmResponse("{\"candidates\":[{\"title\":\"Tighten contract renewals\",\"rationale\":\"seen 3x\",\"confidence\":\"high\"}]}");

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
