// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins which holdout cases the gate actually replays. The budget is finite and OnBeforeSaving stamps
/// CreateTime on insert, so the goldset cases seeded once at startup are forever the oldest rows in the
/// table. Taking the newest ones first therefore pushed exactly the curated population out of the gate as
/// soon as enough cluster-born cases existed - while the count check stayed green, because it counts the
/// same rows the replay no longer sees.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningGoldenCaseRepositoryHoldoutBudgetTests
{
    private const int Budget = 5;
    private const string GoldsetPrefix = "goldset-";
    private const string ClusterPrefix = "cluster-";
    private const string ExpectedSkill = "revenue_per_client";

    private static readonly DateTime Seeded = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Learned = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

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

    private SkillLearningGoldenCaseRepository NewRepository() => new(CreateContext());

    [Test]
    public async Task TheSeededGoldsetCases_SurviveABudgetFullOfNewerClusterCases()
    {
        await GivenSeededGoldsetCases(2);
        await GivenClusterCases(6);

        var holdout = await NewRepository().ListHoldoutAsync(Budget);

        holdout.Count.ShouldBe(Budget);
        holdout.Count(c => c.Origin == GoldenCaseOrigins.Goldset).ShouldBe(2);
        holdout.Select(c => c.Query).ShouldContain(GoldsetPrefix + "0");
        holdout.Select(c => c.Query).ShouldContain(GoldsetPrefix + "1");
    }

    [Test]
    public async Task TheGoldsetCases_ComeFirst()
    {
        await GivenSeededGoldsetCases(2);
        await GivenClusterCases(6);

        var holdout = await NewRepository().ListHoldoutAsync(Budget);

        holdout.Take(2).Select(c => c.Origin).ShouldAllBe(o => o == GoldenCaseOrigins.Goldset);
    }

    [Test]
    public async Task TheRemainingBudget_IsFilledWithTheNewestClusterCases()
    {
        await GivenSeededGoldsetCases(2);
        await GivenClusterCases(6);

        var holdout = await NewRepository().ListHoldoutAsync(Budget);
        var cluster = holdout.Where(c => c.Origin == GoldenCaseOrigins.Cluster).Select(c => c.Query).ToList();

        cluster.Count.ShouldBe(Budget - 2);
        cluster.ShouldContain(ClusterPrefix + "5");
        cluster.ShouldNotContain(ClusterPrefix + "0");
    }

    [Test]
    public async Task MoreGoldsetCasesThanTheBudget_AreStillCutOffAtTheBudget()
    {
        await GivenSeededGoldsetCases(Budget + 3);
        await GivenClusterCases(2);

        var holdout = await NewRepository().ListHoldoutAsync(Budget);

        holdout.Count.ShouldBe(Budget);
        holdout.ShouldAllBe(c => c.Origin == GoldenCaseOrigins.Goldset);
    }

    [Test]
    public async Task ATrainCase_IsNeverReplayed()
    {
        await InsertAsync("train-0", GoldenCaseOrigins.Goldset, GoldenCasePartitions.Train, Seeded);
        await GivenClusterCases(1);

        var holdout = await NewRepository().ListHoldoutAsync(Budget);

        holdout.Select(c => c.Query).ShouldNotContain("train-0");
    }

    private async Task GivenSeededGoldsetCases(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await InsertAsync(
                GoldsetPrefix + index,
                GoldenCaseOrigins.Goldset,
                GoldenCasePartitions.Holdout,
                Seeded.AddMinutes(index));
        }
    }

    private async Task GivenClusterCases(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await InsertAsync(
                ClusterPrefix + index,
                GoldenCaseOrigins.Cluster,
                GoldenCasePartitions.Holdout,
                Learned.AddMinutes(index));
        }
    }

    // OnBeforeSaving stamps CreateTime on every insert, so the age is set afterwards through a tracked
    // entity - a modified row only gets UpdateTime, never a fresh CreateTime.
    private async Task InsertAsync(string query, string origin, string partition, DateTime createTime)
    {
        var goldenCase = new SkillLearningGoldenCase
        {
            Id = Guid.NewGuid(),
            Query = query,
            Locale = "de",
            ExpectedSourceId = ExpectedSkill,
            Origin = origin,
            Partition = partition
        };

        await NewRepository().AddAsync(goldenCase);

        await using var context = CreateContext();
        var stored = await context.SkillLearningGoldenCases.SingleAsync(c => c.Id == goldenCase.Id);
        stored.CreateTime = createTime;
        await context.SaveChangesAsync();
    }
}
