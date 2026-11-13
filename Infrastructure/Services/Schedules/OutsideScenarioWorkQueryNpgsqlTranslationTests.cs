// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the Npgsql translation of OutsideScenarioWorkQuery, which the harmonizer context reads in a scenario run. The
/// behavioural tests run on the in-memory provider, which would stay green if the relational translation failed or dropped
/// a leg of the predicate. ToQueryString generates the SQL offline, no connection is opened.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class OutsideScenarioWorkQueryNpgsqlTranslationTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=unused;Username=u;Password=p";
    private const string WhitespaceRuns = @"\s+";
    private const string SingleSpace = " ";

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
    public void Query_TranslatesEveryLeg()
    {
        var sql = Regex.Replace(
            OutsideScenarioWorkQuery.Query(
                    _context,
                    Guid.NewGuid(),
                    [Guid.NewGuid()],
                    new DateOnly(2026, 3, 9),
                    new DateOnly(2026, 3, 15))
                .ToQueryString(),
            WhitespaceRuns,
            SingleSpace);

        sql.ShouldContain("FROM work");
        Regex.IsMatch(sql, @"\w+\.client_id = ANY").ShouldBeTrue($"Only the agents of the run. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.analyse_token IS NULL").ShouldBeTrue($"Only real works. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.workday >= @").ShouldBeTrue($"Start day inclusive. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.workday <= @").ShouldBeTrue($"End day inclusive. Generated SQL: {sql}");
        sql.ShouldContain("FROM shift");
        Regex.IsMatch(sql, @"\w+\.scenario_source_shift_id").ShouldBeTrue($"The carried shifts come from the scenario's clones. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.analyse_token = @").ShouldBeTrue($"Exactly the scenario's clones. Generated SQL: {sql}");
        Regex.IsMatch(sql, @"\w+\.parent_work_id IS NULL").ShouldBeTrue($"Works that count their own hours. Generated SQL: {sql}");
        sql.ShouldContain("is_deleted");
    }
}
