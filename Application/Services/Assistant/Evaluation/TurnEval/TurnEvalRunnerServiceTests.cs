// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class TurnEvalRunnerServiceTests
{
    private const string GoldsetName = "turn-selection-v1";
    private const string ModelId = "gpt-54-mini";
    private const string ProviderId = "openai";
    private const string ToolName = "add_client_note";
    private const string UserId = "test-user";

    private static readonly List<string> UserRights = ["Admin"];

    private ITurnGoldsetLoader _goldsetLoader = null!;
    private ITurnReplayService _replayService = null!;
    private ISlotEntityResolver _slotEntityResolver = null!;
    private IEvalRunRepository _evalRunRepository = null!;
    private ILogger<TurnEvalRunnerService> _logger = null!;
    private TurnEvalRunnerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _goldsetLoader = Substitute.For<ITurnGoldsetLoader>();
        _replayService = Substitute.For<ITurnReplayService>();
        _slotEntityResolver = Substitute.For<ISlotEntityResolver>();
        _evalRunRepository = Substitute.For<IEvalRunRepository>();
        _logger = Substitute.For<ILogger<TurnEvalRunnerService>>();
        _service = new TurnEvalRunnerService(
            _goldsetLoader, _replayService, _slotEntityResolver, _evalRunRepository, _logger);
    }

    [Test]
    public async Task RunAsync_PersistsRunWithProviderModelGoldsetAndDimensions()
    {
        var items = new List<TurnGoldsetItem>
        {
            new() { Id = "t-1", Message = "add a note", ExpectedTool = ToolName },
            new() { Id = "t-2", Message = "hello" }
        };
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);
        _replayService.ReplayAsync(items[0], ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(SuccessReplay(ToolName));
        _replayService.ReplayAsync(items[1], ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(SuccessReplay(null));

        var result = await _service.RunAsync(GoldsetName, ModelId, null, UserId, UserRights);

        result.Run.Goldset.ShouldBe(GoldsetName);
        result.Run.Model.ShouldBe(ModelId);
        result.Run.Provider.ShouldBe(ProviderId);
        result.Run.ItemsTotal.ShouldBe(2);
        result.Run.ItemsPassed.ShouldBe(2);

        var dimensions = JsonSerializer.Deserialize<TurnEvalDimensions>(result.Run.DimensionsJson);
        dimensions.ShouldNotBeNull();
        dimensions!.ToolAccuracy.ShouldBe(1.0);
        dimensions.NoToolAccuracy.ShouldBe(1.0);

        await _evalRunRepository.Received(1).AddAsync(result.Run, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_WithBaseline_ComputesRegression()
    {
        var items = new List<TurnGoldsetItem>
        {
            new() { Id = "t-1", Message = "add a note", ExpectedTool = ToolName }
        };
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);
        _replayService.ReplayAsync(items[0], ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(SuccessReplay(ToolName));
        _evalRunRepository.GetLatestAsync(GoldsetName, ModelId, Arg.Any<CancellationToken>())
            .Returns(new Klacks.Api.Domain.Models.Assistant.EvalRun { CompositeScore = 0.5m });

        var result = await _service.RunAsync(GoldsetName, ModelId, null, UserId, UserRights);

        result.Run.CompositeScore.ShouldBe(1.0m);
        result.Run.RegressionVsBaseline.ShouldBe(0.5m);
    }

    [Test]
    public async Task RunAsync_WithoutBaseline_RegressionIsNull()
    {
        var items = new List<TurnGoldsetItem>
        {
            new() { Id = "t-1", Message = "hello" }
        };
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);
        _replayService.ReplayAsync(items[0], ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(SuccessReplay(null));

        var result = await _service.RunAsync(GoldsetName, ModelId, null, UserId, UserRights);

        result.Run.RegressionVsBaseline.ShouldBeNull();
    }

    [Test]
    public async Task RunAsync_MaxItems_LimitsReplayCalls()
    {
        var items = new List<TurnGoldsetItem>
        {
            new() { Id = "t-1", Message = "one" },
            new() { Id = "t-2", Message = "two" },
            new() { Id = "t-3", Message = "three" }
        };
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);
        _replayService.ReplayAsync(Arg.Any<TurnGoldsetItem>(), ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(SuccessReplay(null));

        var result = await _service.RunAsync(GoldsetName, ModelId, 1, UserId, UserRights);

        result.Run.ItemsTotal.ShouldBe(1);
        await _replayService.Received(1).ReplayAsync(
            Arg.Any<TurnGoldsetItem>(), ModelId, UserId, UserRights, Arg.Any<CancellationToken>());
        await _replayService.Received(1).ReplayAsync(
            items[0], ModelId, UserId, UserRights, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ResolvedEntitySlot_CallsResolverWithToolParameters()
    {
        var entity = new ExpectedEntityRef("client", 990001);
        var items = new List<TurnGoldsetItem>
        {
            new()
            {
                Id = "t-1",
                Message = "change the phone number of Mrs Muller",
                ExpectedTool = ToolName,
                ExpectedSlots =
                [
                    new TurnGoldsetSlot { Name = "lastName", Match = SlotMatchMode.ResolvedEntityId, Entity = entity }
                ]
            }
        };
        var replay = SuccessReplay(ToolName, new Dictionary<string, object> { ["lastName"] = "Muller" });
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);
        _replayService.ReplayAsync(items[0], ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(replay);
        _slotEntityResolver.ResolvesToExpectedEntityAsync(entity, replay.ToolParameters, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.RunAsync(GoldsetName, ModelId, null, UserId, UserRights);

        await _slotEntityResolver.Received(1).ResolvesToExpectedEntityAsync(
            entity, replay.ToolParameters, Arg.Any<CancellationToken>());
        result.Dimensions.ShouldNotBeNull();
        result.Dimensions!.NameResolutionAccuracy.ShouldBe(1.0);
        result.Run.ItemsPassed.ShouldBe(1);
    }

    [Test]
    public async Task RunAsync_ResolvedEntitySlot_NoToolChosen_ResolverNotCalled()
    {
        var items = new List<TurnGoldsetItem>
        {
            new()
            {
                Id = "t-1",
                Message = "change the phone number of Mrs Muller",
                ExpectedTool = ToolName,
                ExpectedSlots =
                [
                    new TurnGoldsetSlot
                    {
                        Name = "lastName",
                        Match = SlotMatchMode.ResolvedEntityId,
                        Entity = new ExpectedEntityRef("client", 990001)
                    }
                ]
            }
        };
        _goldsetLoader.LoadAsync(GoldsetName, Arg.Any<CancellationToken>()).Returns(items);
        _replayService.ReplayAsync(items[0], ModelId, UserId, UserRights, Arg.Any<CancellationToken>())
            .Returns(SuccessReplay(null));

        var result = await _service.RunAsync(GoldsetName, ModelId, null, UserId, UserRights);

        await _slotEntityResolver.DidNotReceiveWithAnyArgs()
            .ResolvesToExpectedEntityAsync(default!, default!, default);
        result.Run.ItemsPassed.ShouldBe(0);
    }

    private static TurnReplayResult SuccessReplay(string? tool, Dictionary<string, object>? parameters = null)
    {
        return new TurnReplayResult
        {
            Success = true,
            ChosenTool = tool,
            ToolParameters = parameters ?? new Dictionary<string, object>(),
            ProviderId = ProviderId
        };
    }
}
