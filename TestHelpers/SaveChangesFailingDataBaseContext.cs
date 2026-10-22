// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// DataBaseContext over EF InMemory whose next SaveChangesAsync fails with the exception a test hands it, the way
/// PostgreSQL rejects a write the InMemory provider would accept (unique index, concurrency token). The failure runs
/// at save time, after every read the code under test does before the write, so a test can also let a competing
/// writer store its row exactly in that gap. Only the next save fails; later saves go through.
/// </summary>
/// <param name="options">Options of the InMemory database shared by every context of one test</param>
/// <param name="httpContextAccessor">Required by the base constructor</param>
internal sealed class SaveChangesFailingDataBaseContext : DataBaseContext
{
    private const string UniqueViolationSqlState = "23505";
    private const string ErrorSeverity = "ERROR";

    public SaveChangesFailingDataBaseContext(
        DbContextOptions<DataBaseContext> options,
        IHttpContextAccessor httpContextAccessor)
        : base(options, httpContextAccessor)
    {
    }

    public Func<Exception>? FailNextSave { get; set; }

    public static DbUpdateException UniqueViolation() =>
        new(
            "An error occurred while saving the entity changes.",
            new PostgresException("duplicate key value violates unique constraint", ErrorSeverity, ErrorSeverity, UniqueViolationSqlState));

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var failure = FailNextSave;
        if (failure != null)
        {
            FailNextSave = null;
            throw failure();
        }

        return base.SaveChangesAsync(cancellationToken);
    }
}
