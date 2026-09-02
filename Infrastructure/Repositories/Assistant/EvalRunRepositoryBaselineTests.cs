// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for EvalRunRepository.GetBestBaselineAsync, the gate ratchet of the turn evals. Covers:
/// the BEST run wins over the most recent one (with "latest" as baseline every run may legally fall
/// the tolerance below its predecessor, so quality ratchets downwards run by run); partial runs are
/// never a baseline; a different item count, model, goldset or scorer version is not comparable;
/// and equal composites fall back to the newer run so the pick is deterministic.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class EvalRunRepositoryBaselineTests
{
    private const string Goldset = "turn-selection-v1";
    private const string Model = "deepseek-v4-pro";
    private const int ItemsTotal = 334;
    private const int ScorerVersion = 2;

    private static readonly DateTime BaseTime = new(2026, 9, 1, 3, 30, 0, DateTimeKind.Utc);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    [Test]
    public async Task GetBestBaselineAsync_ReturnsHighestComposite_NotTheLatestRun()
    {
        await SeedAsync(
            Run(composite: 0.70m, passed: 240, at: BaseTime),
            Run(composite: 0.42m, passed: 140, at: BaseTime.AddDays(1)));

        var baseline = await NewRepository().GetBestBaselineAsync(Goldset, Model, ItemsTotal, ScorerVersion);

        baseline.ShouldNotBeNull();
        baseline!.CompositeScore.ShouldBe(0.70m);
    }

    [Test]
    public async Task GetBestBaselineAsync_IgnoresPartialRuns()
    {
        await SeedAsync(
            Run(composite: 0.95m, passed: 320, at: BaseTime, isPartial: true),
            Run(composite: 0.50m, passed: 170, at: BaseTime.AddDays(1)));

        var baseline = await NewRepository().GetBestBaselineAsync(Goldset, Model, ItemsTotal, ScorerVersion);

        baseline.ShouldNotBeNull();
        baseline!.CompositeScore.ShouldBe(0.50m);
    }

    [Test]
    public async Task GetBestBaselineAsync_IgnoresOtherItemCountsModelsGoldsetsAndScorerVersions()
    {
        await SeedAsync(
            Run(composite: 0.99m, passed: 60, at: BaseTime, itemsTotal: 60),
            Run(composite: 0.98m, passed: 320, at: BaseTime, model: "gpt-54"),
            Run(composite: 0.97m, passed: 320, at: BaseTime, goldset: "turn-selection-crud-v1"),
            Run(composite: 0.96m, passed: 320, at: BaseTime, scorerVersion: 1),
            Run(composite: 0.30m, passed: 100, at: BaseTime));

        var baseline = await NewRepository().GetBestBaselineAsync(Goldset, Model, ItemsTotal, ScorerVersion);

        baseline.ShouldNotBeNull();
        baseline!.CompositeScore.ShouldBe(0.30m);
    }

    [Test]
    public async Task GetBestBaselineAsync_NoComparableRun_ReturnsNull()
    {
        await SeedAsync(Run(composite: 0.80m, passed: 300, at: BaseTime, scorerVersion: 1));

        var baseline = await NewRepository().GetBestBaselineAsync(Goldset, Model, ItemsTotal, ScorerVersion);

        baseline.ShouldBeNull();
    }

    [Test]
    public async Task GetBestBaselineAsync_EqualComposites_PrefersTheNewerRun()
    {
        await SeedAsync(
            Run(composite: 0.60m, passed: 200, at: BaseTime),
            Run(composite: 0.60m, passed: 201, at: BaseTime.AddDays(1)));

        var baseline = await NewRepository().GetBestBaselineAsync(Goldset, Model, ItemsTotal, ScorerVersion);

        baseline.ShouldNotBeNull();
        baseline!.ItemsPassed.ShouldBe(201);
    }

    private EvalRunRepository NewRepository() => new(CreateContext());

    private async Task SeedAsync(params EvalRun[] runs)
    {
        await using var context = CreateContext();
        context.EvalRuns.AddRange(runs);
        await context.SaveChangesAsync();
    }

    private static EvalRun Run(
        decimal composite,
        int passed,
        DateTime at,
        bool isPartial = false,
        int itemsTotal = ItemsTotal,
        string model = Model,
        string goldset = Goldset,
        int scorerVersion = ScorerVersion) => new()
    {
        Id = Guid.NewGuid(),
        Goldset = goldset,
        Model = model,
        Provider = "deepseek",
        CompositeScore = composite,
        ItemsTotal = itemsTotal,
        ItemsPassed = passed,
        ScorerVersion = scorerVersion,
        IsPartial = isPartial,
        DurationMs = 1000,
        CreateTime = at
    };
}
