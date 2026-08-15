// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// A JobTerminalStateCache together with the two things a test needs to judge it from the outside: how
/// many rows a job id actually left behind, and what the cache logged while getting there.
/// </summary>
/// <param name="cache">The cache under test</param>
/// <param name="logger">Logger the cache writes to, kept to expose its error entries</param>
/// <param name="options">Options of the InMemory database the cache writes to</param>
/// <param name="httpContextAccessor">Required to open a reading context, unused by the cache</param>
internal sealed class JobTerminalStateCacheHarness<TResult>
    where TResult : class
{
    private readonly RecordingLogger<JobTerminalStateCache<TResult>> _logger;
    private readonly DbContextOptions<DataBaseContext> _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public JobTerminalStateCacheHarness(
        JobTerminalStateCache<TResult> cache,
        RecordingLogger<JobTerminalStateCache<TResult>> logger,
        DbContextOptions<DataBaseContext> options,
        IHttpContextAccessor httpContextAccessor)
    {
        Cache = cache;
        _logger = logger;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
    }

    public JobTerminalStateCache<TResult> Cache { get; }

    public IReadOnlyList<RecordedLogEntry> Errors =>
        _logger.Entries.Where(entry => entry.Level >= LogLevel.Error).ToList();

    public async Task<int> CountRowsAsync(Guid jobId)
    {
        await using var context = new DataBaseContext(_options, _httpContextAccessor);
        return await context.JobTerminalStates.AsNoTracking().CountAsync(row => row.JobId == jobId);
    }
}
