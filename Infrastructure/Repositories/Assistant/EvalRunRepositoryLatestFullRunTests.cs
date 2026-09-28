// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for EvalRunRepository.GetLatestFullRunAsync's model filter. The dev database also receives nightly
/// full runs of other models for unrelated model-comparison purposes, so the newest completed run of a
/// goldset is not necessarily a run of the model the caller means - the filter has to pick the newest run
/// of exactly that model, matched case-insensitively because the model id is stored exactly as the provider
/// returned it.
/// </summary>

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class EvalRunRepositoryLatestFullRunTests
{
    private const string Goldset = "turn-selection-v1";
    private const string ReferenceModel = "deepseek-v4-pro";
    private const string OtherModel = "deepseek-flash";
    private const int ItemsTotal = 334;
    private const int ScorerVersion = 2;

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

    private EvalRunRepository NewRepository() => new(CreateContext());

    private async Task SeedAsync(params EvalRun[] runs)
    {
        await using var context = CreateContext();
        context.EvalRuns.AddRange(runs);
        await context.SaveChangesAsync();
    }

    // DataBaseContext stamps CreateTime with its own UtcNow per inserted entity (see
    // EvalRunRepositoryBaselineTests), so the seeded "at" is overwritten and the SEED ORDER is the
    // chronological order these tests assert against; itemsPassed is what a test tells the runs apart by.
    private static EvalRun Run(
        string model,
        int itemsPassed = 0,
        bool isPartial = false,
        int itemsTotal = ItemsTotal,
        string goldset = Goldset,
        int scorerVersion = ScorerVersion) => new()
    {
        Id = Guid.NewGuid(),
        Goldset = goldset,
        Model = model,
        Provider = "deepseek",
        CompositeScore = 0.5m,
        ItemsTotal = itemsTotal,
        ItemsPassed = itemsPassed,
        ScorerVersion = scorerVersion,
        IsPartial = isPartial,
        DurationMs = 1000
    };

    // The exact scenario the setting exists for: a newer run of another model must not shadow the older run
    // of the reference model.
    [Test]
    public async Task ANewerRunOfAnotherModel_DoesNotShadowTheReferenceModelsRun()
    {
        await SeedAsync(
            Run(ReferenceModel, itemsPassed: 100),
            Run(OtherModel, itemsPassed: 200));

        var run = await NewRepository().GetLatestFullRunAsync(Goldset, ScorerVersion, ReferenceModel);

        run.ShouldNotBeNull();
        run!.Model.ShouldBe(ReferenceModel);
    }

    [Test]
    public async Task TheNewestRunOfTheReferenceModel_Wins()
    {
        await SeedAsync(
            Run(ReferenceModel, itemsPassed: 100),
            Run(ReferenceModel, itemsPassed: 150),
            Run(OtherModel, itemsPassed: 200));

        var run = await NewRepository().GetLatestFullRunAsync(Goldset, ScorerVersion, ReferenceModel);

        run.ShouldNotBeNull();
        run!.ItemsPassed.ShouldBe(150);
    }

    [Test]
    public async Task WithoutAnyRunOfTheReferenceModel_NullIsReturned()
    {
        await SeedAsync(Run(OtherModel));

        var run = await NewRepository().GetLatestFullRunAsync(Goldset, ScorerVersion, ReferenceModel);

        run.ShouldBeNull();
    }

    [Test]
    public async Task TheModelMatch_IsCaseInsensitive()
    {
        await SeedAsync(Run("DeepSeek-V4-Pro"));

        var run = await NewRepository().GetLatestFullRunAsync(Goldset, ScorerVersion, ReferenceModel);

        run.ShouldNotBeNull();
    }

    [Test]
    public async Task APartialRunOfTheReferenceModel_IsIgnored()
    {
        await SeedAsync(Run(ReferenceModel, isPartial: true));

        var run = await NewRepository().GetLatestFullRunAsync(Goldset, ScorerVersion, ReferenceModel);

        run.ShouldBeNull();
    }
}
