// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the query-time turn_id join of the fitness counters. was_successful is a capture-time
/// snapshot, and the UiAction outcome report (W1.4) arrives afterwards - so a turn whose only execution
/// the browser later reported as failed must stop counting as a success, while a still-dispatched turn
/// stays neutral and a legacy turn without a turn_id keeps its old semantics.
/// </summary>

using Klacks.Api.Domain.Enums;
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
public class SkillSelectionTrajectoryFitnessJoinTests
{
    private const string OwnerName = "create_client";
    private const string RecipeName = "onboard_client";
    private static readonly DateTime StartUtc = new(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStartUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

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

    private SkillSelectionTrajectoryRepository CreateRepository() => new(CreateContext());

    private static SkillSelectionTrajectory MakeTrajectory(Guid? turnId, bool? wasSuccessful) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        TurnId = turnId,
        UserId = "user-1",
        Locale = "de",
        UserMessageHash = "a1b2c3d4e5f60718",
        IntentExcerpt = "Lege einen neuen Kunden an",
        LearnedPhraseHit = OwnerName,
        LlmChosenSkill = OwnerName,
        RecipeName = RecipeName,
        WasExecuted = true,
        WasSuccessful = wasSuccessful,
        WasCorrected = false,
        CreateTime = StartUtc
    };

    private static SkillUsageRecord MakeUsage(Guid turnId, bool success, UiActionStatus? uiActionStatus) => new()
    {
        Id = Guid.NewGuid(),
        SkillName = OwnerName,
        Category = SkillCategory.Action,
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        TurnId = turnId,
        Success = success,
        UiActionStatus = uiActionStatus,
        DurationMs = 12,
        Timestamp = StartUtc
    };

    private async Task SeedAsync(SkillSelectionTrajectory trajectory, SkillUsageRecord? usage)
    {
        await using var seed = CreateContext();
        seed.SkillSelectionTrajectories.Add(trajectory);
        if (usage != null)
        {
            seed.SkillUsageRecords.Add(usage);
        }

        await seed.SaveChangesAsync();
    }

    [Test]
    public async Task PhraseUsage_LateUiActionFailure_IsNoLongerCountedAsASuccess()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), MakeUsage(turnId, success: false, UiActionStatus.Failed));

        var usage = await CreateRepository().CountPhraseUsageAsync(OwnerName, WindowStartUtc);

        usage.Uses.ShouldBe(1);
        usage.Successes.ShouldBe(0);
    }

    [Test]
    public async Task PhraseUsage_StillDispatchedUiAction_StaysNeutralAndCounts()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), MakeUsage(turnId, success: false, UiActionStatus.Dispatched));

        var usage = await CreateRepository().CountPhraseUsageAsync(OwnerName, WindowStartUtc);

        usage.Uses.ShouldBe(1);
        usage.Successes.ShouldBe(1);
    }

    [Test]
    public async Task PhraseUsage_CancelledUiActionOfAStoppedTurn_StaysNeutral()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), MakeUsage(turnId, success: false, UiActionStatus.Cancelled));

        var usage = await CreateRepository().CountPhraseUsageAsync(OwnerName, WindowStartUtc);

        usage.Uses.ShouldBe(1);
        usage.Successes.ShouldBe(1);
    }

    [Test]
    public async Task PhraseUsage_ACancelledSkillRowOfAStoppedTurn_StaysNeutral()
    {
        var turnId = Guid.NewGuid();
        var cancelled = MakeUsage(turnId, success: false, uiActionStatus: null);
        cancelled.FailureKind = SkillFailureKind.Cancelled;
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), cancelled);

        var usage = await CreateRepository().CountPhraseUsageAsync(OwnerName, WindowStartUtc);

        usage.Successes.ShouldBe(1);
    }

    [Test]
    public async Task RecipeUsage_CancelledUiActionOfAStoppedTurn_StaysNeutral()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), MakeUsage(turnId, success: false, UiActionStatus.Cancelled));

        var usage = await CreateRepository().CountRecipeUsageAsync(RecipeName, WindowStartUtc);

        usage.Successes.ShouldBe(1);
    }

    [Test]
    public async Task PhraseUsage_LegacyTurnWithoutATurnId_KeepsTheOldSemantics()
    {
        await SeedAsync(MakeTrajectory(turnId: null, wasSuccessful: null), usage: null);

        var usage = await CreateRepository().CountPhraseUsageAsync(OwnerName, WindowStartUtc);

        usage.Uses.ShouldBe(1);
        usage.Successes.ShouldBe(1);
    }

    [Test]
    public async Task RecipeUsage_LateUiActionFailure_IsNoLongerCountedAsASuccess()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), MakeUsage(turnId, success: false, UiActionStatus.Failed));

        var usage = await CreateRepository().CountRecipeUsageAsync(RecipeName, WindowStartUtc);

        usage.Uses.ShouldBe(1);
        usage.Successes.ShouldBe(0);
    }

    [Test]
    public async Task HasSuccessfulRecipeTurn_LateUiActionFailure_IsFalse()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: null), MakeUsage(turnId, success: false, UiActionStatus.Failed));

        var result = await CreateRepository().HasSuccessfulRecipeTurnAsync(RecipeName);

        result.ShouldBeFalse();
    }

    [Test]
    public async Task HasSuccessfulRecipeTurn_SuccessfulExecution_IsTrue()
    {
        var turnId = Guid.NewGuid();
        await SeedAsync(MakeTrajectory(turnId, wasSuccessful: true), MakeUsage(turnId, success: true, uiActionStatus: null));

        var result = await CreateRepository().HasSuccessfulRecipeTurnAsync(RecipeName);

        result.ShouldBeTrue();
    }
}
