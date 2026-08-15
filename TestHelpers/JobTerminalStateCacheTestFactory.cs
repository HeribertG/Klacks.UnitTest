// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Builds a JobTerminalStateCache backed by a fresh EF InMemory DataBaseContext. The cache resolves a new
/// scope per operation, so the scope factory hands out a new context over the same in-memory database on
/// every call, mirroring the production scoped-DbContext registration.
/// </summary>
internal static class JobTerminalStateCacheTestFactory
{
    public static JobTerminalStateCache<TResult> Create<TResult>()
        where TResult : class
    {
        var options = NewOptions();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();

        // NullLogger rather than a substitute: Castle DynamicProxy cannot proxy a logger generic over a
        // non-public test result type, and no test on this path asserts on the logger.
        return NewCache(
            options,
            httpContextAccessor,
            NullLogger<JobTerminalStateCache<TResult>>.Instance,
            () => new DataBaseContext(options, httpContextAccessor));
    }

    /// <summary>
    /// Same cache, but over contexts that enforce the unique index on JobId, which the InMemory provider
    /// otherwise ignores. Needed by every test about "first terminal state wins": that rule lives in the
    /// index plus the duplicate branch in StoreAsync, and neither is reachable without a store that
    /// actually rejects the second row.
    /// </summary>
    public static JobTerminalStateCacheHarness<TResult> CreateWithUniqueJobIdIndex<TResult>()
        where TResult : class
    {
        var options = NewOptions();
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var logger = new RecordingLogger<JobTerminalStateCache<TResult>>();

        var cache = NewCache(
            options,
            httpContextAccessor,
            logger,
            () => new UniqueJobIdEnforcingDataBaseContext(options, httpContextAccessor));

        return new JobTerminalStateCacheHarness<TResult>(cache, logger, options, httpContextAccessor);
    }

    private static DbContextOptions<DataBaseContext> NewOptions() =>
        new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private static JobTerminalStateCache<TResult> NewCache<TResult>(
        DbContextOptions<DataBaseContext> options,
        IHttpContextAccessor httpContextAccessor,
        ILogger<JobTerminalStateCache<TResult>> logger,
        Func<DataBaseContext> contextFactory)
        where TResult : class
    {
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(DataBaseContext)).Returns(_ => contextFactory());
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return new JobTerminalStateCache<TResult>(scopeFactory, logger);
    }
}
