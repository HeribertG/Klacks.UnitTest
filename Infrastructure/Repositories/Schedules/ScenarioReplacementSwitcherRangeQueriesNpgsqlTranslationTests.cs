// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves on the real Npgsql provider that the two range reads behind the undo of a swap chain translate: the works a
/// scenario moved away from a person and the replacements on that person's works, each over an inclusive range of
/// workdays, exact on the scenario token, top-level works only and in a stable order. ToQueryString needs no server, so
/// the context points at an unreachable one.
/// </summary>

using Klacks.Api.Infrastructure.Services.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class ScenarioReplacementSwitcherRangeQueriesNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private static readonly DateOnly From = new(2026, 12, 8);
    private static readonly DateOnly Until = new(2026, 12, 22);

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [Test]
    public void MovedWorksOfARange_FilterByTokenOriginRangeAndTopLevel_InTheDatabase()
    {
        // Arrange
        using var context = NewContext();
        var repository = new WorkRepository(
            context, Substitute.For<ILogger<Work>>(), Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(), Substitute.For<IClientContractDataProvider>());

        // Act
        var sql = repository.QueryMovedInScenario(Guid.NewGuid(), Guid.NewGuid(), From, Until).ToQueryString();

        // Assert
        sql.ShouldContain("analyse_token = @");
        sql.ShouldContain("moved_from_client_id = @");
        sql.ShouldContain("workday >= @");
        sql.ShouldContain("workday <= @");
        sql.ShouldContain("parent_work_id IS NULL");
        sql.ShouldContain("is_deleted");
        sql.ShouldContain("ORDER BY");
    }

    [Test]
    public void ReplacementsOfARange_FilterByTokenOwnerRangeAndTopLevel_InTheDatabase()
    {
        // Arrange
        using var context = NewContext();
        var repository = new WorkChangeRepository(
            context, Substitute.For<ILogger<WorkChange>>(), Substitute.For<IWorkMacroService>());

        // Act
        var sql = repository.QueryScenarioReplacements(Guid.NewGuid(), Guid.NewGuid(), From, Until).ToQueryString();

        // Assert
        sql.ShouldContain("analyse_token = @");
        sql.ShouldContain("client_id = @");
        sql.ShouldContain("workday >= @");
        sql.ShouldContain("workday <= @");
        sql.ShouldContain("parent_work_id IS NULL");
        sql.ShouldContain("replace_client_id IS NOT NULL");
        sql.ShouldContain("ORDER BY");
    }
}
