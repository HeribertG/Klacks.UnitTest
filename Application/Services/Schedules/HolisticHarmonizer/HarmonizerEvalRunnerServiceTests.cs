// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

[TestFixture]
public class HarmonizerEvalRunnerServiceTests
{
    private const string ModelId = "test-model";
    private const int ScenarioCount = 3;
    private const decimal ParseOnlyComposite = 0.2m;

    private IPlanProposalProvider _proposalProvider = null!;
    private IEvalRunRepository _evalRunRepository = null!;
    private HarmonizerEvalRunnerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _proposalProvider = Substitute.For<IPlanProposalProvider>();
        _evalRunRepository = Substitute.For<IEvalRunRepository>();
        _evalRunRepository
            .GetLatestAsync(HarmonizerEvalGoldset.Name, ModelId, Arg.Any<CancellationToken>())
            .Returns((EvalRun?)null);
        _service = new HarmonizerEvalRunnerService(
            _proposalProvider,
            _evalRunRepository,
            Substitute.For<ILogger<HarmonizerEvalRunnerService>>());
    }

    [Test]
    public async Task RunAsync_ProviderEchoesFirstCandidate_PersistsEvalRunWithGoldsetAndModel()
    {
        SetupProviderEchoingFirstCandidate();
        EvalRun? persisted = null;
        await _evalRunRepository.AddAsync(Arg.Do<EvalRun>(r => persisted = r), Arg.Any<CancellationToken>());

        var result = await _service.RunAsync(ModelId);

        await _evalRunRepository.Received(1).AddAsync(Arg.Any<EvalRun>(), Arg.Any<CancellationToken>());
        persisted.ShouldNotBeNull();
        persisted!.Goldset.ShouldBe(HarmonizerEvalGoldset.Name);
        persisted.Model.ShouldBe(ModelId);
        persisted.ItemsTotal.ShouldBe(ScenarioCount);
        persisted.CompositeScore.ShouldBeInRange(ParseOnlyComposite, 1m);
        persisted.DimensionsJson.ShouldContain(nameof(HarmonizerEvalDimensions.ParseRate));
        result.Dimensions.ParseRate.ShouldBe(1m);
        result.Dimensions.LlmCallsTotal.ShouldBeGreaterThan(0);
        result.Dimensions.BatchesProposed.ShouldBeGreaterThan(0);
        result.Scenarios.Count.ShouldBe(ScenarioCount);
    }

    [Test]
    public async Task RunAsync_ProviderReturnsEmptyBatches_CompositeIsParseWeightOnly()
    {
        _proposalProvider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse([], string.Empty, null));

        var result = await _service.RunAsync(ModelId);

        result.Run.CompositeScore.ShouldBe(ParseOnlyComposite);
        result.Run.ItemsPassed.ShouldBe(0);
        result.Dimensions.ParseRate.ShouldBe(1m);
        result.Dimensions.BatchAcceptanceRate.ShouldBe(0m);
        result.Dimensions.NormalizedFitnessImprovement.ShouldBe(0m);
    }

    [Test]
    public async Task RunAsync_ProviderAlwaysFailsParsing_CompositeIsZeroAndRunIsStillPersisted()
    {
        _proposalProvider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse([], "garbage", "no JSON object found"));

        var result = await _service.RunAsync(ModelId);

        result.Run.CompositeScore.ShouldBe(0m);
        result.Run.ItemsPassed.ShouldBe(0);
        result.Dimensions.LlmCallsParsed.ShouldBe(0);
        result.Scenarios.ShouldAllBe(s => s.LastError != null);
        await _evalRunRepository.Received(1).AddAsync(Arg.Any<EvalRun>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_BaselineExists_ComputesRegressionAgainstBaseline()
    {
        var baselineComposite = 0.5m;
        _evalRunRepository
            .GetLatestAsync(HarmonizerEvalGoldset.Name, ModelId, Arg.Any<CancellationToken>())
            .Returns(new EvalRun { Goldset = HarmonizerEvalGoldset.Name, Model = ModelId, CompositeScore = baselineComposite });
        _proposalProvider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse([], string.Empty, null));

        var result = await _service.RunAsync(ModelId);

        result.Run.RegressionVsBaseline.ShouldBe(ParseOnlyComposite - baselineComposite);
    }

    [Test]
    public async Task RunAsync_NoBaseline_RegressionIsNull()
    {
        _proposalProvider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanProposalResponse([], string.Empty, null));

        var result = await _service.RunAsync(ModelId);

        result.Run.RegressionVsBaseline.ShouldBeNull();
    }

    private void SetupProviderEchoingFirstCandidate()
    {
        _proposalProvider
            .ProposeAsync(Arg.Any<PlanProposalRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.Arg<PlanProposalRequest>();
                var candidates = request.CandidateMoves;
                if (candidates is null || candidates.Count == 0)
                {
                    return new PlanProposalResponse([], string.Empty, null);
                }

                var first = candidates[0];
                var batch = new MutationBatch(
                    Guid.NewGuid(),
                    request.FocusedIntent,
                    request.IterationIndex,
                    [new PlanCellSwap(first.RowA, first.DayA, first.RowB, first.DayB, first.Hint)]);
                return new PlanProposalResponse([batch], string.Empty, null);
            });
    }
}
