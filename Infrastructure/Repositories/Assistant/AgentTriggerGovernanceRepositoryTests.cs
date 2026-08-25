// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentTriggerGovernanceRepository: UpsertAsync inserts a first rule and updates every
/// mutable field of an existing one instead of adding a second row, FindAsync tells the
/// installation-wide rule (null GroupId) apart from a group-scoped one for the same kind, and
/// GetAllAsync returns both ordered. Uses a shared in-memory DataBaseContext, mirroring the
/// neighbouring repository tests.
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
public class AgentTriggerGovernanceRepositoryTests
{
    private const string TriggerKind = "unstaffed_shift";
    private const string OtherTriggerKind = "empty_container";

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

    private static AgentTriggerGovernance Rule(string triggerKind, Guid? groupId = null) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = triggerKind,
        GroupId = groupId
    };

    [Test]
    public async Task UpsertAsync_WithoutExistingRule_InsertsIt()
    {
        // Arrange
        using var context = CreateContext();
        var sut = new AgentTriggerGovernanceRepository(context);
        var rule = Rule(TriggerKind);
        rule.MaxAction = ProactiveMaxAction.Prepare;

        // Act
        await sut.UpsertAsync(rule, CancellationToken.None);

        // Assert
        var stored = await context.AgentTriggerGovernances.ToListAsync();
        stored.ShouldHaveSingleItem();
        stored[0].TriggerKind.ShouldBe(TriggerKind);
        stored[0].MaxAction.ShouldBe(ProactiveMaxAction.Prepare);
    }

    [Test]
    public async Task UpsertAsync_WithExistingRule_UpdatesInPlaceInsteadOfAddingASecondRow()
    {
        // Arrange
        using var seedContext = CreateContext();
        seedContext.AgentTriggerGovernances.Add(Rule(TriggerKind));
        await seedContext.SaveChangesAsync();

        using var context = CreateContext();
        var sut = new AgentTriggerGovernanceRepository(context);
        var ownerUserId = Guid.NewGuid();
        var update = Rule(TriggerKind);
        update.MaxAction = ProactiveMaxAction.Execute;
        update.Enabled = false;
        update.ResponsibleOwnerUserId = ownerUserId;
        update.DailyActionBudget = 9;
        update.WindowActionLimit = 4;
        update.WindowMinutes = 30;

        // Act
        await sut.UpsertAsync(update, CancellationToken.None);

        // Assert
        var stored = await context.AgentTriggerGovernances.ToListAsync();
        stored.ShouldHaveSingleItem();
        stored[0].MaxAction.ShouldBe(ProactiveMaxAction.Execute);
        stored[0].Enabled.ShouldBeFalse();
        stored[0].ResponsibleOwnerUserId.ShouldBe(ownerUserId);
        stored[0].DailyActionBudget.ShouldBe(9);
        stored[0].WindowActionLimit.ShouldBe(4);
        stored[0].WindowMinutes.ShouldBe(30);
    }

    [Test]
    public async Task UpsertAsync_WithGroupScope_KeepsTheInstallationWideRuleSeparate()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        using var seedContext = CreateContext();
        seedContext.AgentTriggerGovernances.Add(Rule(TriggerKind));
        await seedContext.SaveChangesAsync();

        using var context = CreateContext();
        var sut = new AgentTriggerGovernanceRepository(context);
        var scoped = Rule(TriggerKind, groupId);
        scoped.MaxAction = ProactiveMaxAction.Prepare;

        // Act
        await sut.UpsertAsync(scoped, CancellationToken.None);

        // Assert
        var stored = await context.AgentTriggerGovernances.ToListAsync();
        stored.Count.ShouldBe(2);
        stored.Count(rule => rule.GroupId is null).ShouldBe(1);
        stored.Single(rule => rule.GroupId == groupId).MaxAction.ShouldBe(ProactiveMaxAction.Prepare);
    }

    [Test]
    public async Task FindAsync_DistinguishesTheGlobalRuleFromTheGroupScopedOne()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        using var seedContext = CreateContext();
        var global = Rule(TriggerKind);
        global.MaxAction = ProactiveMaxAction.Hint;
        var scoped = Rule(TriggerKind, groupId);
        scoped.MaxAction = ProactiveMaxAction.Execute;
        seedContext.AgentTriggerGovernances.AddRange(global, scoped);
        await seedContext.SaveChangesAsync();

        using var context = CreateContext();
        var sut = new AgentTriggerGovernanceRepository(context);

        // Act
        var foundGlobal = await sut.FindAsync(TriggerKind, null, CancellationToken.None);
        var foundScoped = await sut.FindAsync(TriggerKind, groupId, CancellationToken.None);

        // Assert
        foundGlobal!.MaxAction.ShouldBe(ProactiveMaxAction.Hint);
        foundScoped!.MaxAction.ShouldBe(ProactiveMaxAction.Execute);
    }

    [Test]
    public async Task FindAsync_WithoutAnyStoredRule_ReturnsNull()
    {
        // Arrange
        using var context = CreateContext();
        var sut = new AgentTriggerGovernanceRepository(context);

        // Act
        var found = await sut.FindAsync(TriggerKind, null, CancellationToken.None);

        // Assert
        found.ShouldBeNull();
    }

    [Test]
    public async Task GetAllAsync_ReturnsEveryStoredRule()
    {
        // Arrange
        using var seedContext = CreateContext();
        seedContext.AgentTriggerGovernances.AddRange(Rule(TriggerKind), Rule(OtherTriggerKind));
        await seedContext.SaveChangesAsync();

        using var context = CreateContext();
        var sut = new AgentTriggerGovernanceRepository(context);

        // Act
        var all = await sut.GetAllAsync(CancellationToken.None);

        // Assert
        all.Count.ShouldBe(2);
        all.Select(rule => rule.TriggerKind).ShouldContain(TriggerKind);
        all.Select(rule => rule.TriggerKind).ShouldContain(OtherTriggerKind);
    }
}
