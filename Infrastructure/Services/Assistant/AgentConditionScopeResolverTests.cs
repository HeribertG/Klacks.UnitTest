// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for AgentConditionScopeResolver: an Admin is unrestricted regardless of GroupVisibility rows, an
/// Authorised planner is restricted to their own visible root ids, a planner with zero GroupVisibility
/// rows resolves to an empty (fail-closed) set rather than "sees everything", a plain User (or unknown
/// account) is not a planner at all, and other users' GroupVisibility rows never leak into the result.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class AgentConditionScopeResolverTests
{
    private const string AdminUserId = "admin-1";
    private const string ScopedPlannerUserId = "planner-scoped";
    private const string NoRowPlannerUserId = "planner-no-row";
    private const string PlainUserId = "plain-user";
    private const string UnknownUserId = "unknown-user";

    private static readonly Guid RootGroupId = Guid.NewGuid();

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;
    private UserManager<AppUser> _userManager = null!;
    private IMemoryCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();

        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errors = Substitute.For<IdentityErrorDescriber>();
        var services = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errors, services, logger);

        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [TearDown]
    public void TearDown()
    {
        _userManager?.Dispose();
        _cache?.Dispose();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private static AppUser MakeUser(string id) => new() { Id = id, UserName = id, Email = $"{id}@test.local" };

    private void SetupUser(string id, params string[] roles)
    {
        var user = MakeUser(id);
        _userManager.FindByIdAsync(id).Returns(Task.FromResult<AppUser?>(user));
        _userManager.GetRolesAsync(user).Returns(Task.FromResult<IList<string>>(roles.ToList()));
    }

    [Test]
    public async Task Admin_IsUnrestricted_RegardlessOfGroupVisibilityRows()
    {
        SetupUser(AdminUserId, Roles.Admin);
        using var context = CreateContext();

        var scope = await new AgentConditionScopeResolver(context, _userManager, _cache).ResolveAsync(AdminUserId);

        scope.IsPlanner.ShouldBeTrue();
        scope.IsUnrestricted.ShouldBeTrue();
    }

    [Test]
    public async Task AuthorisedPlanner_WithVisibilityRows_IsRestrictedToOwnRootIds()
    {
        SetupUser(ScopedPlannerUserId, Roles.Authorised);
        using var context = CreateContext();
        context.GroupVisibility.Add(new GroupVisibility { AppUserId = ScopedPlannerUserId, GroupId = RootGroupId });
        await context.SaveChangesAsync();

        var scope = await new AgentConditionScopeResolver(context, _userManager, _cache).ResolveAsync(ScopedPlannerUserId);

        scope.IsPlanner.ShouldBeTrue();
        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleRootIds.ShouldContain(RootGroupId);
    }

    [Test]
    public async Task AuthorisedPlanner_WithNoVisibilityRows_ResolvesToEmptySet_FailClosed()
    {
        SetupUser(NoRowPlannerUserId, Roles.Authorised);
        using var context = CreateContext();

        var scope = await new AgentConditionScopeResolver(context, _userManager, _cache).ResolveAsync(NoRowPlannerUserId);

        scope.IsPlanner.ShouldBeTrue();
        scope.IsUnrestricted.ShouldBeFalse();
        scope.VisibleRootIds.ShouldBeEmpty();
    }

    [Test]
    public async Task AuthorisedPlanner_NeverSeesAnotherUsersVisibilityRows_NegativeTest()
    {
        SetupUser(NoRowPlannerUserId, Roles.Authorised);
        using var context = CreateContext();
        context.GroupVisibility.Add(new GroupVisibility { AppUserId = "someone-else", GroupId = RootGroupId });
        await context.SaveChangesAsync();

        var scope = await new AgentConditionScopeResolver(context, _userManager, _cache).ResolveAsync(NoRowPlannerUserId);

        scope.VisibleRootIds.ShouldNotContain(RootGroupId);
    }

    [Test]
    public async Task PlainUser_IsNotAPlanner()
    {
        SetupUser(PlainUserId, Roles.User);
        using var context = CreateContext();

        var scope = await new AgentConditionScopeResolver(context, _userManager, _cache).ResolveAsync(PlainUserId);

        scope.IsPlanner.ShouldBeFalse();
    }

    [Test]
    public async Task UnknownUser_IsNotAPlanner()
    {
        _userManager.FindByIdAsync(UnknownUserId).Returns(Task.FromResult<AppUser?>(null));
        using var context = CreateContext();

        var scope = await new AgentConditionScopeResolver(context, _userManager, _cache).ResolveAsync(UnknownUserId);

        scope.IsPlanner.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAsync_CalledTwiceForSameUser_ResolvesRolesOnlyOnce()
    {
        SetupUser(ScopedPlannerUserId, Roles.Authorised);
        using var context = CreateContext();
        context.GroupVisibility.Add(new GroupVisibility { AppUserId = ScopedPlannerUserId, GroupId = RootGroupId });
        await context.SaveChangesAsync();
        var sut = new AgentConditionScopeResolver(context, _userManager, _cache);

        await sut.ResolveAsync(ScopedPlannerUserId);
        await sut.ResolveAsync(ScopedPlannerUserId);

        await _userManager.Received(1).FindByIdAsync(ScopedPlannerUserId);
    }
}
