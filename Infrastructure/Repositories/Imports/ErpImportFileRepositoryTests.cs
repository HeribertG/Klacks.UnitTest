// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the argument guard of ErpImportFileRepository.FinishAsync: only Completed,
/// CompletedWithWarnings, Failed and Interrupted may finish a file, and the guard fires before any database access
/// (the context points at an unreachable server). The conditional updates themselves are proven against
/// PostgreSQL in Klacks.IntegrationTest (ErpImportFileRepositoryTests).
/// </summary>

using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Repositories.Imports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Imports;

[TestFixture]
public class ErpImportFileRepositoryTests
{
    private const string UnusedConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin";

    private const string InstanceId = "unit-test:instance";

    [TestCase(ErpImportFileStatus.Received)]
    [TestCase(ErpImportFileStatus.Importing)]
    public async Task FinishAsync_WithANonFinishStatus_ThrowsBeforeTouchingTheDatabase(ErpImportFileStatus status)
    {
        await using var context = new DataBaseContext(
            new DbContextOptionsBuilder<DataBaseContext>()
                .UseNpgsql(UnusedConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options,
            Substitute.For<IHttpContextAccessor>());
        var repository = new ErpImportFileRepository(context);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => repository.FinishAsync(
            Guid.NewGuid(), InstanceId, 1, status, ErpImportFileProgress.Empty, null, DateTime.UtcNow));
    }
}
