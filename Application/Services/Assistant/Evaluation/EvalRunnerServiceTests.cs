// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for EvalRunnerService composite scoring + persistence using mocked retrieval and repository.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation;

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class EvalRunnerServiceTests
{
    private IKnowledgeRetrievalService _retrieval = null!;
    private IEvalRunRepository _evalRunRepository = null!;
    private IGoldsetLoader _goldsetLoader = null!;
    private ILogger<EvalRunnerService> _logger = null!;
    private EvalRunnerService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _retrieval = Substitute.For<IKnowledgeRetrievalService>();
        _evalRunRepository = Substitute.For<IEvalRunRepository>();
        _goldsetLoader = Substitute.For<IGoldsetLoader>();
        _logger = Substitute.For<ILogger<EvalRunnerService>>();
        _service = new EvalRunnerService(_retrieval, _evalRunRepository, _goldsetLoader, _logger);
    }

    [Test]
    public async Task RunAsync_AllItemsHitTop1_ProducesNearMaxScore()
    {
        var items = new List<GoldsetItem>
        {
            new("create employee", "create_employee"),
            new("show branches", "list_branches")
        };
        _goldsetLoader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(items);
        _retrieval.RetrieveAsync("create employee", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult("create_employee", "search_employees"));
        _retrieval.RetrieveAsync("show branches", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult("list_branches", "create_branch"));

        var run = await _service.RunAsync("test-goldset");

        run.ItemsTotal.ShouldBe(2);
        run.ItemsPassed.ShouldBe(2);
        run.CompositeScore.ShouldBeGreaterThan(0.85m);

        var dims = JsonSerializer.Deserialize<EvalDimensions>(run.DimensionsJson);
        dims.ShouldNotBeNull();
        dims!.Top1Hit.ShouldBe(1.0);
        dims.Top3Hit.ShouldBe(1.0);
    }

    [Test]
    public async Task RunAsync_HitInTop3ButNotTop1_ScoresPartial()
    {
        var items = new List<GoldsetItem>
        {
            new("create employee", "create_employee")
        };
        _goldsetLoader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(items);
        _retrieval.RetrieveAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult("search_employees", "list_branches", "create_employee"));

        var run = await _service.RunAsync("test-goldset");

        var dims = JsonSerializer.Deserialize<EvalDimensions>(run.DimensionsJson)!;
        dims.Top1Hit.ShouldBe(0.0);
        dims.Top3Hit.ShouldBe(1.0);
        run.ItemsPassed.ShouldBe(1);
    }

    [Test]
    public async Task RunAsync_PersistsRunWithBaselineRegression()
    {
        var items = new List<GoldsetItem> { new("foo", "bar") };
        _goldsetLoader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(items);
        _retrieval.RetrieveAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(MakeResult("bar"));
        _evalRunRepository.GetLatestAsync("test-goldset", Arg.Any<CancellationToken>())
            .Returns(new EvalRun { CompositeScore = 0.5m });

        var run = await _service.RunAsync("test-goldset");

        run.RegressionVsBaseline.ShouldNotBeNull();
        run.RegressionVsBaseline!.Value.ShouldBeGreaterThan(0m);
        await _evalRunRepository.Received(1).AddAsync(Arg.Any<EvalRun>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_EmptyRetrieval_DoesNotCrash()
    {
        var items = new List<GoldsetItem> { new("unknown", "missing_skill") };
        _goldsetLoader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(items);
        _retrieval.RetrieveAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new RetrievalResult([]));

        var run = await _service.RunAsync("test-goldset");

        run.ItemsTotal.ShouldBe(1);
        run.ItemsPassed.ShouldBe(0);
        var dims = JsonSerializer.Deserialize<EvalDimensions>(run.DimensionsJson)!;
        dims.Top1Hit.ShouldBe(0.0);
    }

    private static RetrievalResult MakeResult(params string[] sourceIds)
    {
        var candidates = sourceIds.Select((id, idx) =>
            new RetrievalCandidate(
                new KnowledgeEntry { SourceId = id, Kind = KnowledgeEntryKind.Skill, Text = id, TextHash = [] },
                Score: 1.0 - (idx * 0.1))).ToList();
        return new RetrievalResult(candidates);
    }
}
