// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// DataBaseContext over EF InMemory that rejects a second row for a job id the way PostgreSQL does,
/// because the InMemory provider ignores unique indexes and would happily store both. Whether it rejects
/// is read from the model's own index metadata rather than hard-coded, so deleting
/// <c>HasIndex(p =&gt; p.JobId).IsUnique()</c> from the entity configuration also switches this
/// enforcement off - a test using it then sees two rows and fails, instead of staying green over a rule
/// that no longer exists.
/// </summary>
/// <param name="options">Options of the InMemory database shared by every context of one test</param>
/// <param name="httpContextAccessor">Required by the base constructor, unused by the cache</param>
internal sealed class UniqueJobIdEnforcingDataBaseContext : DataBaseContext
{
    private const string UniqueViolationSqlState = "23505";
    private const string ErrorSeverity = "ERROR";
    private const string UniqueJobIdIndexName = "ix_job_terminal_states_job_id";

    public UniqueJobIdEnforcingDataBaseContext(
        DbContextOptions<DataBaseContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options, httpContextAccessor)
    {
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var duplicate = FindDuplicateJobId();
        if (duplicate != null)
        {
            throw UniqueViolation(duplicate.Value);
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Shaped like the real thing on both counts the production code inspects: a DbUpdateException whose
    /// inner exception is a PostgresException carrying SQLSTATE 23505. Anything less specific would be
    /// caught by the outer safety net instead of the duplicate branch under test.
    /// </summary>
    private static DbUpdateException UniqueViolation(Guid jobId) =>
        new(
            $"An error occurred while saving the entity changes for job {jobId}.",
            new PostgresException(
                $"duplicate key value violates unique constraint \"{UniqueJobIdIndexName}\"",
                ErrorSeverity,
                ErrorSeverity,
                UniqueViolationSqlState));

    private bool ModelDeclaresUniqueJobIdIndex() =>
        Model.FindEntityType(typeof(JobTerminalStateRow))?
            .GetIndexes()
            .Any(index =>
                index.IsUnique
                && index.Properties.Count == 1
                && index.Properties[0].Name == nameof(JobTerminalStateRow.JobId))
        ?? false;

    private Guid? FindDuplicateJobId()
    {
        if (!ModelDeclaresUniqueJobIdIndex())
        {
            return null;
        }

        var addedJobIds = ChangeTracker.Entries<JobTerminalStateRow>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.JobId)
            .ToList();

        if (addedJobIds.Count == 0)
        {
            return null;
        }

        var alreadyStored = JobTerminalStates
            .AsNoTracking()
            .Where(row => addedJobIds.Contains(row.JobId))
            .Select(row => row.JobId)
            .Take(1)
            .ToList();

        return alreadyStored.Count == 0 ? null : alreadyStored[0];
    }
}
