// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SkillEffectivenessRepository, the windowed aggregation behind the "Skill-Wirksamkeit"
/// scorecard (W6). Three properties are load-bearing and invisible in the numbers once they break: a
/// UiAction still waiting for the browser report must not be counted as a call (its Success flag means
/// "not confirmed yet", not "failed"), a row whose UiActionStatus is null must stay in, and a
/// soft-deleted telemetry row must disappear from every counter - which is what the added
/// SkillUsageRecordConfiguration query filter is for.
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class SkillEffectivenessRepositoryTests
{
    private const string SkillName = "list_clients";
    private const string OtherSkillName = "list_shifts";
    private const string RecipeName = "absence_request";

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = Now.AddDays(-SkillEffectivenessDefaults.DefaultDays);
    private static readonly DateTime InsideWindow = WindowStart.AddDays(1);
    private static readonly DateTime BeforeWindow = WindowStart.AddDays(-1);

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

    // A dispatched UiAction has not been confirmed by any browser yet. Counting it as a call turns every
    // pending action into a failure the moment the report is late, which is exactly the false signal the
    // W1.4 lifecycle was introduced to remove. A row without a UiActionStatus is a normal skill call and
    // must survive the same predicate.
    [Test]
    public async Task ADispatchedUiAction_IsExcludedWhileANonUiActionRowStaysIn()
    {
        await SeedUsageAsync(MakeUsage(uiActionStatus: null), InsideWindow);
        await SeedUsageAsync(MakeUsage(uiActionStatus: UiActionStatus.Completed), InsideWindow);
        await SeedUsageAsync(MakeUsage(uiActionStatus: UiActionStatus.Failed, success: false), InsideWindow);
        await SeedUsageAsync(MakeUsage(uiActionStatus: UiActionStatus.Dispatched), InsideWindow);

        var stats = await CreateRepository().GetSkillCallStatsAsync(WindowStart);

        var stat = stats.ShouldHaveSingleItem();
        stat.SkillName.ShouldBe(SkillName);
        stat.Calls.ShouldBe(3);
        stat.Failures.ShouldBe(1);
    }

    [Test]
    public async Task ARowOlderThanTheWindow_IsOutsideTheReport()
    {
        await SeedUsageAsync(MakeUsage(), InsideWindow);
        await SeedUsageAsync(MakeUsage(skillName: OtherSkillName), BeforeWindow);

        var repository = CreateRepository();

        (await repository.GetUsageCountAsync(WindowStart)).ShouldBe(1);
        (await repository.GetSkillCallStatsAsync(WindowStart))
            .Select(s => s.SkillName).ShouldBe(new[] { SkillName });
    }

    [Test]
    public async Task ARowExactlyOnTheWindowStart_IsInsideTheReport()
    {
        await SeedUsageAsync(MakeUsage(), WindowStart);

        (await CreateRepository().GetUsageCountAsync(WindowStart)).ShouldBe(1);
    }

    // The twin assertion is the point: IgnoreQueryFilters proves the soft-deleted row is really in the
    // store, so the zero above comes from the query filter and not from a seed that never landed.
    [Test]
    public async Task ASoftDeletedRow_IsInvisibleToEveryCounterButStillStored()
    {
        await SeedUsageAsync(MakeUsage(), InsideWindow);
        await SeedUsageAsync(
            MakeUsage(skillName: OtherSkillName, success: false, failureKind: SkillFailureKind.NotFound, isDeleted: true),
            InsideWindow);

        var repository = CreateRepository();

        (await repository.GetUsageCountAsync(WindowStart)).ShouldBe(1);
        (await repository.GetSkillCallStatsAsync(WindowStart))
            .Select(s => s.SkillName).ShouldBe(new[] { SkillName });
        (await repository.GetFailureCountsAsync(WindowStart)).ShouldBeEmpty();

        await using var context = CreateContext();
        (await context.SkillUsageRecords.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
    }

    [Test]
    public async Task AFailureKind_IsCountedPerClassAndOnlyWhereOneIsSet()
    {
        await SeedUsageAsync(MakeUsage(success: false, failureKind: SkillFailureKind.NotFound), InsideWindow);
        await SeedUsageAsync(MakeUsage(success: false, failureKind: SkillFailureKind.NotFound), InsideWindow);
        await SeedUsageAsync(MakeUsage(success: false, failureKind: SkillFailureKind.PermissionDenied), InsideWindow);
        await SeedUsageAsync(MakeUsage(), InsideWindow);

        var counts = await CreateRepository().GetFailureCountsAsync(WindowStart);

        counts.Count.ShouldBe(2);
        counts.Single(c => c.Kind == SkillFailureKind.NotFound).Count.ShouldBe(2);
        counts.Single(c => c.Kind == SkillFailureKind.PermissionDenied).Count.ShouldBe(1);
    }

    // A pre-dispatch failure row is written with the failure class and never gets Success cleared, so a
    // failure count that only looks at Success would silently report those rows as successful calls.
    [Test]
    public async Task ARowWithAFailureKind_CountsAsAFailureEvenWhenSuccessIsTrue()
    {
        await SeedUsageAsync(MakeUsage(success: true, failureKind: SkillFailureKind.GateHold), InsideWindow);
        await SeedUsageAsync(MakeUsage(success: false), InsideWindow);
        await SeedUsageAsync(MakeUsage(), InsideWindow);

        var stat = (await CreateRepository().GetSkillCallStatsAsync(WindowStart)).ShouldHaveSingleItem();

        stat.Calls.ShouldBe(3);
        stat.Failures.ShouldBe(2);
    }

    [Test]
    public async Task TheRecipeFunnel_CountsEveryStatusInsideTheWindow()
    {
        await SeedRecipeRunAsync(RecipeRunStatus.Running, InsideWindow);
        await SeedRecipeRunAsync(RecipeRunStatus.Completed, InsideWindow);
        await SeedRecipeRunAsync(RecipeRunStatus.Completed, InsideWindow);
        await SeedRecipeRunAsync(RecipeRunStatus.Aborted, InsideWindow);
        await SeedRecipeRunAsync(RecipeRunStatus.Expired, InsideWindow);
        await SeedRecipeRunAsync(RecipeRunStatus.Completed, BeforeWindow);

        var row = (await CreateRepository().GetRecipeFunnelAsync(WindowStart)).ShouldHaveSingleItem();

        row.RecipeName.ShouldBe(RecipeName);
        row.Started.ShouldBe(5);
        row.Running.ShouldBe(1);
        row.Completed.ShouldBe(2);
        row.Aborted.ShouldBe(1);
        row.Expired.ShouldBe(1);
    }

    [Test]
    public async Task TheEvalTrend_IsNewestFirstAndBoundedByTheLimit()
    {
        await SeedEvalRunAsync("oldest", Now.AddDays(-3));
        await SeedEvalRunAsync("newest", Now.AddDays(-1));
        await SeedEvalRunAsync("middle", Now.AddDays(-2));

        var trend = await CreateRepository().GetEvalTrendAsync(2);

        trend.Select(e => e.Goldset).ShouldBe(new[] { "newest", "middle" });
    }

    [Test]
    public async Task TheTrajectorySample_IsNewestFirstInsideTheWindowAndBoundedByTheLimit()
    {
        await SeedTrajectoryAsync("older", InsideWindow);
        await SeedTrajectoryAsync("newer", InsideWindow.AddDays(1));
        await SeedTrajectoryAsync("outside", BeforeWindow);

        var sample = await CreateRepository().GetChosenSourceSampleAsync(WindowStart, 2);

        sample.Select(s => s.LlmChosenSkill).ShouldBe(new[] { "newer", "older" });
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private SkillEffectivenessRepository CreateRepository() => new(CreateContext());

    private static SkillUsageRecord MakeUsage(
        string skillName = SkillName,
        bool success = true,
        UiActionStatus? uiActionStatus = null,
        SkillFailureKind? failureKind = null,
        bool isDeleted = false) => new()
        {
            Id = Guid.NewGuid(),
            SkillName = skillName,
            Category = SkillCategory.Query,
            UserId = UserId,
            TenantId = TenantId,
            Success = success,
            UiActionStatus = uiActionStatus,
            FailureKind = failureKind,
            IsDeleted = isDeleted,
            Timestamp = Now
        };

    private Task SeedUsageAsync(SkillUsageRecord record, DateTime createTime) =>
        SeedAsync(context => context.SkillUsageRecords.Add(record), record, createTime);

    private Task SeedRecipeRunAsync(RecipeRunStatus status, DateTime createTime)
    {
        var run = new RecipeRun
        {
            Id = Guid.NewGuid(),
            RecipeName = RecipeName,
            UserId = UserId,
            ConversationId = Guid.NewGuid().ToString(),
            Status = status
        };

        return SeedAsync(context => context.RecipeRuns.Add(run), run, createTime);
    }

    private Task SeedEvalRunAsync(string goldset, DateTime createTime)
    {
        var run = new EvalRun
        {
            Id = Guid.NewGuid(),
            Goldset = goldset,
            CompositeScore = 0.5m,
            ItemsTotal = 10,
            ItemsPassed = 5
        };

        return SeedAsync(context => context.EvalRuns.Add(run), run, createTime);
    }

    private Task SeedTrajectoryAsync(string chosenSkill, DateTime createTime)
    {
        var trajectory = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            Locale = "de",
            LlmChosenSkill = chosenSkill
        };

        return SeedAsync(context => context.SkillSelectionTrajectories.Add(trajectory), trajectory, createTime);
    }

    // DataBaseContext.OnBeforeSaving overwrites CreateTime with UtcNow for every inserted entity, so a
    // create time seeded inline never reaches the store. The second save runs as a modification, which
    // only touches UpdateTime and therefore keeps the window boundary the test is about.
    private async Task SeedAsync(Action<DataBaseContext> add, BaseEntity entity, DateTime createTime)
    {
        await using var context = CreateContext();
        add(context);
        await context.SaveChangesAsync();

        entity.CreateTime = createTime;
        await context.SaveChangesAsync();
    }
}
