// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the caller scope of the trajectory lookup the correction menu and the correction endpoint share.
/// The same sentence is typed by many users, so the utterance hash alone is not a key: without the user
/// id in the predicate the most recently captured turn of a stranger would decide which options one user
/// is offered and which trajectory their correction marks.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class SkillSelectionTrajectoryRepositoryUserScopeTests
{
    private const string FirstUserId = "user-1";
    private const string SecondUserId = "user-2";
    private const string UserWithoutATurn = "user-3";
    private const string Message = "Lege einen neuen Kunden an";

    private static readonly DateTime CapturedAtUtc = new(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc);

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

    private static SkillSelectionTrajectory MakeTrajectory(string userId, string chosenSkill) => new()
    {
        Id = Guid.NewGuid(),
        AgentId = Guid.NewGuid(),
        UserId = userId,
        Locale = "de",
        UserMessageHash = MessageNormalizer.Hash(Message),
        IntentExcerpt = Message,
        LlmChosenSkill = chosenSkill,
        CreateTime = CapturedAtUtc
    };

    private async Task SeedBothUsersAsync()
    {
        await using var seed = CreateContext();
        seed.SkillSelectionTrajectories.Add(MakeTrajectory(FirstUserId, "list_clients"));
        seed.SkillSelectionTrajectories.Add(MakeTrajectory(SecondUserId, "create_client"));
        await seed.SaveChangesAsync();
    }

    [Test]
    public async Task TwoUsersWithTheSameUtterance_EachGetTheirOwnTrajectory()
    {
        await SeedBothUsersAsync();
        var hash = MessageNormalizer.Hash(Message);

        var first = await CreateRepository().FindMostRecentByUserAndHashAsync(FirstUserId, hash);
        var second = await CreateRepository().FindMostRecentByUserAndHashAsync(SecondUserId, hash);

        first.ShouldNotBeNull();
        first!.UserId.ShouldBe(FirstUserId);
        first.LlmChosenSkill.ShouldBe("list_clients");
        second.ShouldNotBeNull();
        second!.UserId.ShouldBe(SecondUserId);
        second.LlmChosenSkill.ShouldBe("create_client");
    }

    [Test]
    public async Task TheTurnIdLookup_NamesTheStoppedTurnEvenWhenItWasStoredAfterItsTwinWithTheSameHash()
    {
        var stoppedTurnId = Guid.NewGuid();
        var resentTurnId = Guid.NewGuid();
        var stopped = MakeTrajectory(FirstUserId, "delete_client");
        stopped.TurnId = stoppedTurnId;
        stopped.WasInterrupted = true;
        var resent = MakeTrajectory(FirstUserId, "list_clients");
        resent.TurnId = resentTurnId;
        await using (var seed = CreateContext())
        {
            seed.SkillSelectionTrajectories.Add(resent);
            await seed.SaveChangesAsync();
        }
        await using (var lateSeed = CreateContext())
        {
            lateSeed.SkillSelectionTrajectories.Add(stopped);
            await lateSeed.SaveChangesAsync();
        }
        var repository = CreateRepository();

        var byHash = await repository.FindMostRecentByUserAndHashAsync(FirstUserId, MessageNormalizer.Hash(Message));
        var byStoppedTurn = await repository.FindByUserAndTurnIdAsync(FirstUserId, stoppedTurnId);
        var byResentTurn = await repository.FindByUserAndTurnIdAsync(FirstUserId, resentTurnId);

        byHash!.Id.ShouldBe(stopped.Id);
        byStoppedTurn!.Id.ShouldBe(stopped.Id);
        byResentTurn!.Id.ShouldBe(resent.Id);
        byResentTurn.LlmChosenSkill.ShouldBe("list_clients");
    }

    [Test]
    public async Task TheTurnIdLookup_DoesNotFindTheTurnOfAnotherUser()
    {
        var turnId = Guid.NewGuid();
        var foreign = MakeTrajectory(SecondUserId, "create_client");
        foreign.TurnId = turnId;
        await using (var seed = CreateContext())
        {
            seed.SkillSelectionTrajectories.Add(foreign);
            await seed.SaveChangesAsync();
        }

        var asOwner = await CreateRepository().FindByUserAndTurnIdAsync(SecondUserId, turnId);
        var asStranger = await CreateRepository().FindByUserAndTurnIdAsync(FirstUserId, turnId);

        asOwner.ShouldNotBeNull();
        asStranger.ShouldBeNull();
    }

    [Test]
    public async Task TheTurnIdLookup_DoesNotFindAnUnknownTurnOrARowWithoutATurnId()
    {
        await SeedBothUsersAsync();

        var unknown = await CreateRepository().FindByUserAndTurnIdAsync(FirstUserId, Guid.NewGuid());
        var empty = await CreateRepository().FindByUserAndTurnIdAsync(FirstUserId, Guid.Empty);

        unknown.ShouldBeNull();
        empty.ShouldBeNull();
    }

    [Test]
    public async Task AUserWithoutACapturedTurn_GetsNothingFromAnotherUsersRow()
    {
        await SeedBothUsersAsync();

        var result = await CreateRepository().FindMostRecentByUserAndHashAsync(
            UserWithoutATurn, MessageNormalizer.Hash(Message));

        result.ShouldBeNull();
    }
}
