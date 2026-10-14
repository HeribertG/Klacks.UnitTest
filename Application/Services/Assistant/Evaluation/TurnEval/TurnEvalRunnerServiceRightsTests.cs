// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that the runner replays every item with the rights its role resolves to (and an item without a role with
/// the run's own list), and that it returns the per-origin dimensions next to the unchanged whole-run aggregate.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalRunnerServiceRightsTests
{
    private const string GoldsetName = "turn-rights-test";
    private const string ModelId = "gpt-54-mini";
    private const string UserId = "test-user";
    private const string ToolName = "add_client_note";
    private const string RefusalAnswer = "Dafür hast du keine Berechtigung.";

    private static readonly List<string> RunRights = [Roles.Admin];

    private ITurnReplayService _replayService = null!;
    private TurnEvalRunnerService _service = null!;
    private List<List<string>> _replayedRights = null!;

    [SetUp]
    public void SetUp()
    {
        _replayedRights = [];
        _replayService = Substitute.For<ITurnReplayService>();
        _replayService
            .ReplayWithLookupFollowUpAsync(
                Arg.Any<TurnGoldsetItem>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _replayedRights.Add(call.Arg<List<string>>());
                var item = call.Arg<TurnGoldsetItem>();
                return Task.FromResult(new TurnReplayResult
                {
                    Success = true,
                    ChosenTool = item.ExpectsRightsRefusal ? null : item.ExpectedTool,
                    Content = item.ExpectsRightsRefusal ? RefusalAnswer : string.Empty,
                    AvailableToolNames = item.ExpectedTool == null ? [] : [item.ExpectedTool],
                    ProviderId = "openai"
                });
            });

        var evalRunRepository = Substitute.For<IEvalRunRepository>();
        evalRunRepository
            .GetComparableCompositesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _service = new TurnEvalRunnerService(
            Substitute.For<ITurnGoldsetLoader>(), _replayService, Substitute.For<ISlotEntityResolver>(),
            evalRunRepository, Substitute.For<IEvalRunItemRepository>(),
            Substitute.For<ILogger<TurnEvalRunnerService>>());
    }

    private static List<TurnGoldsetItem> MixedItems() =>
    [
        new() { Id = "h-1", Message = "note", ExpectedTool = ToolName, Source = TurnGoldsetSources.Curated },
        new()
        {
            Id = "r-1", Message = "change settings", Role = TurnGoldsetRole.Planner, ExpectsRightsRefusal = true,
            DeniedSkill = "update_general_settings", Source = TurnGoldsetSources.Curated
        },
        new()
        {
            Id = "s-1", Message = "note again", ExpectedTool = ToolName, Source = "w0.5-seed-coverage",
            Role = TurnGoldsetRole.Supervisor
        }
    ];

    [Test]
    public async Task RunAsync_ReplaysEachItemWithTheRightsOfItsRole()
    {
        await _service.RunAsync(GoldsetName, MixedItems(), ModelId, null, UserId, RunRights);

        _replayedRights.Count.ShouldBe(3);
        _replayedRights[0].ShouldBeSameAs(RunRights);
        _replayedRights[1].ShouldBe(Permissions.PlannerFloor.ToList());
        _replayedRights[2].ShouldContain(Roles.Authorised);
        _replayedRights[2].ShouldNotContain(Roles.Admin);
    }

    [Test]
    public async Task RunAsync_ReturnsPerOriginDimensionsAndLeavesTheWholeRunAggregateWhole()
    {
        var result = await _service.RunAsync(GoldsetName, MixedItems(), ModelId, null, UserId, RunRights);

        result.DimensionsByOrigin.Keys.ShouldBe([TurnGoldsetOrigin.Handwritten, TurnGoldsetOrigin.Synthetic]);
        result.DimensionsByOrigin[TurnGoldsetOrigin.Handwritten].ItemsTotal.ShouldBe(2);
        result.DimensionsByOrigin[TurnGoldsetOrigin.Handwritten].RightsRefusalAccuracy.ShouldBe(1.0);
        result.DimensionsByOrigin[TurnGoldsetOrigin.Synthetic].ItemsTotal.ShouldBe(1);
        result.DimensionsByOrigin[TurnGoldsetOrigin.Synthetic].RightsRefusalAccuracy.ShouldBeNull();
        result.Dimensions!.ItemsTotal.ShouldBe(3);
        result.Dimensions.ItemsPassed.ShouldBe(3);
        result.Dimensions.RightsRefusalAccuracy.ShouldBe(1.0);
    }

    [Test]
    public async Task RunAsync_PersistedDimensionsOfAGoldsetWithoutRightsItemsCarryNoRightsField()
    {
        var items = new List<TurnGoldsetItem>
        {
            new() { Id = "h-1", Message = "note", ExpectedTool = ToolName, Source = TurnGoldsetSources.Curated }
        };

        var result = await _service.RunAsync(GoldsetName, items, ModelId, null, UserId, RunRights);

        result.Run.DimensionsJson.ShouldNotContain(nameof(TurnEvalDimensions.RightsRefusalAccuracy));
        JsonSerializer.Deserialize<TurnEvalDimensions>(result.Run.DimensionsJson)!.ToolAccuracy.ShouldBe(1.0);
    }
}
