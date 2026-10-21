// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Proves that every query of the staleness check translates to SQL on the real Npgsql provider (the in-memory provider
/// of the behaviour tests evaluates any C# expression). The connection is never opened: an interceptor suppresses the
/// open and answers every command with an empty result, so each query is translated and "executed" in turn and an
/// untranslatable expression would surface as "could not be translated". With no changed rows the check ends after the
/// five change queries; the day-lock plan and the accepted-sibling query that follow a change run against real Postgres
/// in the integration test (AcceptDayLockSeamTests).
/// </summary>

using Klacks.UnitTest.TestHelpers;
using System.Data;
using System.Data.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioServiceStalenessNpgsqlTranslationTests
{
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=klacks_model_only;Username=postgres;Password=admin;Timeout=2;Command Timeout=2";

    private DataBaseContext _context = null!;
    private EmptyResultInterceptor _interceptor = null!;

    [SetUp]
    public void SetUp()
    {
        _interceptor = new EmptyResultInterceptor();
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnreachableConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(_interceptor)
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task StalenessCheck_TranslatesEveryQuery(bool grouped, bool builtOnAScenario)
    {
        var service = AnalyseScenarioTestServiceFactory.Create(_context);
        var scenario = new AnalyseScenario
        {
            Id = Guid.NewGuid(),
            Token = Guid.NewGuid(),
            GroupId = grouped ? Guid.NewGuid() : null,
            FromDate = new DateOnly(2026, 3, 9),
            UntilDate = new DateOnly(2026, 3, 15),
            Status = AnalyseScenarioStatus.Active,
            CreateTime = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
            SourceToken = builtOnAScenario ? Guid.NewGuid() : null
        };

        var staleness = await service.FindRealPlanChangesSinceCreationAsync(scenario, CancellationToken.None);

        staleness.ShouldBeNull();
        _interceptor.ExecutedCommands.ShouldContain(sql => sql.Contains("work_change"));
        _interceptor.ExecutedCommands.ShouldContain(sql => sql.Contains("expenses"));
        _interceptor.ExecutedCommands.ShouldContain(sql => sql.Contains("schedule_note"));
        _interceptor.ExecutedCommands.ShouldContain(sql => sql.Contains("break"));
        if (builtOnAScenario)
        {
            _interceptor.ExecutedCommands.ShouldContain(sql => sql.Contains("source_token"));
        }
    }

    /// <summary>
    /// Answers every command with an empty result.
    /// </summary>
    private sealed class EmptyResultInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public List<string> ExecutedCommands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ExecutedCommands.Add(command.CommandText);
            var table = new DataTable();

            return InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader());
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ReaderExecuting(command, eventData, result));

        public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }
}