// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of the scenario-from-scenario clone filters. The clone tests run on the in-memory
/// provider, which accepts LINQ Npgsql cannot translate; the model is built against Npgsql without opening a connection
/// and ToQueryString generates the SQL offline. Without a source token the filters stay the plain real-plan filter.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioCloneSourceSqlTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string TokenIsNull = "analyse_token IS NULL";

    private static readonly ScenarioCloneSource Source = new(Guid.NewGuid(), new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(UnusedConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public void DatedFilters_FromASource_TranslateTheTokenAndDateSplit()
    {
        var queries = new[]
        {
            AnalyseScenarioService.WhereFromSource(_context.Work, Source).ToQueryString(),
            AnalyseScenarioService.WhereFromSource(_context.Break, Source).ToQueryString(),
            AnalyseScenarioService.WhereFromSource(_context.ScheduleNotes, Source).ToQueryString(),
            AnalyseScenarioService.WhereFromSource(_context.ScheduleCommands, Source).ToQueryString(),
        };

        foreach (var sql in queries.Select(Normalize))
        {
            sql.ShouldContain("analyse_token = ", Case.Insensitive, sql);
            sql.ShouldContain(TokenIsNull, Case.Insensitive, sql);
            sql.ShouldContain(">= @from", Case.Insensitive, sql);
            sql.ShouldContain("> @until", Case.Insensitive, sql);
            sql.ShouldContain(" OR ", Case.Insensitive, sql);
        }
    }

    [Test]
    public void UndatedFilters_FromASource_TranslateEitherWorld()
    {
        var queries = new[]
        {
            AnalyseScenarioService.WhereEitherWorld(_context.Work, Source).ToQueryString(),
            AnalyseScenarioService.WhereEitherWorld(_context.WorkChange, Source).ToQueryString(),
            AnalyseScenarioService.WhereEitherWorld(_context.Expenses, Source).ToQueryString(),
            AnalyseScenarioService.WhereEitherWorld(_context.Shift, Source).ToQueryString(),
            AnalyseScenarioService.WhereEitherWorld(_context.Set<Klacks.Api.Domain.Models.Associations.GroupItem>(), Source).ToQueryString(),
            AnalyseScenarioService.WhereEitherWorld(_context.Set<Klacks.Api.Domain.Models.Associations.ClientShiftPreference>(), Source).ToQueryString(),
            AnalyseScenarioService.WhereEitherWorld(_context.Set<Klacks.Api.Domain.Models.Schedules.ShiftExpenses>(), Source).ToQueryString(),
        };

        foreach (var sql in queries.Select(Normalize))
        {
            sql.ShouldContain(TokenIsNull, Case.Insensitive, sql);
            sql.ShouldContain(" OR ", Case.Insensitive, sql);
        }
    }

    [Test]
    public void ClientScopeFilters_FromASource_LimitRealRowsToRealMembers()
    {
        var clients = (Real: new List<Guid> { Guid.NewGuid() }, WithSource: new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });
        var queries = new[]
        {
            AnalyseScenarioService.WhereClientInScope(_context.Break, clients, Source).ToQueryString(),
            AnalyseScenarioService.WhereClientInScope(_context.ScheduleNotes, clients, Source).ToQueryString(),
            AnalyseScenarioService.WhereClientInScope(_context.ScheduleCommands, clients, Source).ToQueryString(),
        };

        foreach (var sql in queries.Select(Normalize))
        {
            sql.ShouldContain(TokenIsNull, Case.Insensitive, sql);
            sql.ShouldContain("analyse_token IS NOT NULL", Case.Insensitive, sql);
            sql.ShouldContain("client_id", Case.Insensitive, sql);
        }
    }

    [Test]
    public void Filters_OfTheRealPlan_StayThePlainRealPlanFilter()
    {
        var work = Normalize(AnalyseScenarioService.WhereFromSource(_context.Work, ScenarioCloneSource.RealPlan).ToQueryString());
        var change = Normalize(AnalyseScenarioService.WhereEitherWorld(_context.WorkChange, ScenarioCloneSource.RealPlan).ToQueryString());

        foreach (var sql in new[] { work, change })
        {
            sql.ShouldContain(TokenIsNull, Case.Insensitive, sql);
            sql.ShouldNotContain(" OR ", Case.Insensitive, sql);
            sql.ShouldNotContain("@from", Case.Insensitive, sql);
        }
    }

    private static string Normalize(string sql) => Regex.Replace(sql, @"\s+", " ");
}
