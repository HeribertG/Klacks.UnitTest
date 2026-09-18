// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class KlacksyModelCheckServiceEvalScoreTests
{
    private const string Goldset = "turn-selection-v1";
    private const string PartialRunModel = "model-with-partial-run";
    private const string FullRunModel = "model-with-full-run";

    [Test]
    public async Task LoadEvalScoresAsync_IgnoresAPartialRun()
    {
        var repository = Substitute.For<IEvalRunRepository>();
        repository.GetLatestPerModelAsync(Goldset, Arg.Any<CancellationToken>())
            .Returns(new List<EvalRun>
            {
                new() { Model = PartialRunModel, CompositeScore = 0.11m, IsPartial = true },
                new() { Model = FullRunModel, CompositeScore = 0.84m }
            });

        var scores = await CreateService(repository).LoadEvalScoresAsync(CancellationToken.None);

        scores.ContainsKey(PartialRunModel).ShouldBeFalse();
        scores[FullRunModel].ShouldBe(0.84m);
    }

    [Test]
    public async Task LoadEvalScoresAsync_WhenTheRepositoryFails_RanksHeuristicallyInstead()
    {
        var repository = Substitute.For<IEvalRunRepository>();
        repository.GetLatestPerModelAsync(Goldset, Arg.Any<CancellationToken>())
            .Returns<List<EvalRun>>(_ => throw new InvalidOperationException("eval store unreachable"));

        var scores = await CreateService(repository).LoadEvalScoresAsync(CancellationToken.None);

        scores.ShouldBeEmpty();
    }

    private static KlacksyModelCheckService CreateService(IEvalRunRepository repository) =>
        new(null!, null!, repository, NullLogger<KlacksyModelCheckService>.Instance);
}
